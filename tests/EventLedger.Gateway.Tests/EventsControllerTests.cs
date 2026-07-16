using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using EventLedger.Gateway.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventLedger.Gateway.Tests;

public class EventsControllerTests : IDisposable
{
    private readonly SqliteTempDbFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private WebApplicationFactory<Program> CreateFactory() =>
        CreateFactory(new FlakyAccountServiceHandler(failuresBeforeSuccess: 0));

    private WebApplicationFactory<Program> CreateFactory(HttpMessageHandler accountServiceHandler) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<GatewayDbContext>>();
                services.AddDbContext<GatewayDbContext>(opt => opt.UseSqlite(_fixture.ConnectionString));

                services.AddHttpClient("AccountService")
                    .ConfigurePrimaryHttpMessageHandler(() => accountServiceHandler);
            });
        });

    private static object ValidPayload(string eventId = "evt-1", string accountId = "acct-1") => new
    {
        eventId,
        accountId,
        type = "CREDIT",
        amount = 100m,
        currency = "USD",
        eventTimestamp = "2026-05-15T14:02:11Z"
    };

    [Fact]
    public async Task PostEvents_ValidPayload_Returns201WithFullRecord()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/events", ValidPayload());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EventResponseDto>();
        Assert.Equal("evt-1", body!.EventId);
        Assert.Equal("CREDIT", body.Type);
        Assert.Equal(100m, body.Amount);
    }

    [Fact]
    public async Task PostEvents_InvalidPayload_Returns400WithErrorShape()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/events", new { eventId = "evt-2", accountId = "acct-1", type = "CREDIT", amount = 0m, currency = "USD", eventTimestamp = "2026-05-15T14:02:11Z" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        Assert.Equal("validation_error", body!.Error);
        Assert.NotNull(body.Message);
    }

    [Fact]
    public async Task PostEvents_MissingRequiredField_Returns400WithErrorShape()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/events", new { accountId = "acct-1", type = "CREDIT", amount = 100m, currency = "USD", eventTimestamp = "2026-05-15T14:02:11Z" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        Assert.Equal("validation_error", body!.Error);
    }

    [Fact]
    public async Task PostEvents_InvalidType_Returns400WithErrorShape()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/events", new { eventId = "evt-invalid-type", accountId = "acct-1", type = "PAYMENT", amount = 100m, currency = "USD", eventTimestamp = "2026-05-15T14:02:11Z" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        Assert.Equal("validation_error", body!.Error);
    }

    [Fact]
    public async Task GetEventById_ExistingId_Returns200WithRecord()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/events", ValidPayload("evt-3"));

        var response = await client.GetAsync("/events/evt-3");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EventResponseDto>();
        Assert.Equal("evt-3", body!.EventId);
    }

    [Fact]
    public async Task GetEventById_NonExistentId_Returns404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/events/evt-missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetEventsByAccount_ReturnsArrayOrderedAscendingByEventTimestamp()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/events", ValidPayload("evt-later"));
        await PostWithTimestamp(client, "evt-earliest", "2026-05-10T00:00:00Z");
        await PostWithTimestamp(client, "evt-middle", "2026-05-12T00:00:00Z");

        var response = await client.GetAsync("/events?account=acct-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<EventResponseDto>>();
        Assert.Equal(["evt-earliest", "evt-middle", "evt-later"], body!.Select(e => e.EventId));
    }

    // RES-2: a hung Account Service call is bounded by the resilience pipeline's timeout, not
    // indefinite. The handler hangs for 30s — far longer than the pipeline could ever take (2s
    // per-attempt timeout x 3 attempts + 200ms retry delays x 2 ~= 6.4s worst case) — so a
    // response arriving well under that hang duration proves the timeout, not the hang,
    // determined it. Asserting CallCount == 3 (not just the elapsed-time bound) is what actually
    // proves the timeout is applied per attempt, with retry still engaging after each one — a
    // total-operation timeout wrapping the whole retry sequence (a real bug this test caught: the
    // timeout was originally registered outer to retry, cutting a hung call off after exactly 1
    // attempt in ~2s) would also satisfy a loose "< 10s" bound alone.
    [Fact]
    public async Task PostEvents_AccountServiceHangs_TimesOutAndReturns503()
    {
        var handler = new HangingAccountServiceHandler(TimeSpan.FromSeconds(30));
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync("/events", ValidPayload("evt-hang"));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Expected the pipeline's timeout to bound the response well under the handler's 30s hang, but it took {stopwatch.Elapsed}.");
        Assert.Equal(3, handler.CallCount);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        Assert.Equal("account_service_unavailable", body!.Error);
    }

    // RES-5: a single transient failure recovers via a small, bounded retry — the caller never
    // sees it. Asserting CallCount == 2 (not just the final 201) is what actually proves retry
    // happened, rather than the first call happening to succeed on its own.
    [Fact]
    public async Task PostEvents_AccountServiceFailsOnceThenSucceeds_RetryRecoversTransparently()
    {
        var handler = new FlakyAccountServiceHandler(failuresBeforeSuccess: 1);
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/events", ValidPayload("evt-flaky-recovers"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, handler.CallCount);
    }

    // RES-3: sustained failures open the circuit; a subsequent call fails immediately with no
    // network attempt. The circuit breaker is the OUTERMOST strategy, so it observes one outcome
    // per POST /events call (after that call's own internal retries are exhausted), not one per
    // retry attempt — 4 always-failing calls is enough to hit the breaker's minimum throughput (4)
    // at a 100% failure ratio and trip it open. Asserting the handler's call count doesn't increase
    // for the next call is what actually proves no network attempt was made, not just that the
    // response was 503 (which an ordinary failed call would also produce).
    [Fact]
    public async Task PostEvents_SustainedFailures_OpensCircuitAndFailsFastWithoutNetworkAttempt()
    {
        var handler = new FlakyAccountServiceHandler(failuresBeforeSuccess: int.MaxValue);
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();

        await TripCircuitOpenAsync(client, "evt-circuit");

        var callCountBeforeTrip = handler.CallCount;

        var response = await client.PostAsJsonAsync("/events", ValidPayload("evt-circuit-open-check"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(callCountBeforeTrip, handler.CallCount);
    }

    // RES-4: after the circuit's break duration elapses, it half-opens; a successful trial call
    // closes it again. Trips the circuit the same way as RES-3, waits past the 5s break duration
    // (a real wait, per this story's timing-strategy decision), then flips the handler to succeed
    // before issuing the trial call.
    [Fact]
    public async Task PostEvents_CircuitOpensThenCooldownElapses_HalfOpensAndClosesOnSuccess()
    {
        var handler = new FlakyAccountServiceHandler(failuresBeforeSuccess: int.MaxValue);
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();

        await TripCircuitOpenAsync(client, "evt-cooldown");

        await Task.Delay(TimeSpan.FromSeconds(8)); // past the 5s break duration, with margin for a slow/loaded runner

        handler.FailuresBeforeSuccess = 0; // the half-open trial call, and everything after, succeeds
        var response = await client.PostAsJsonAsync("/events", ValidPayload("evt-cooldown-trial"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // RES-6: an Account Service outage must not just return 503 — it must leave nothing behind.
    // Asserting the follow-up GET returns 404 (not just that POST returned 503) is what actually
    // proves persistence never happened, rather than just that the response code was right.
    [Fact]
    public async Task PostEvents_AccountServiceUnreachable_Returns503AndPersistsNothing()
    {
        using var factory = CreateFactory(new FlakyAccountServiceHandler(failuresBeforeSuccess: int.MaxValue));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/events", ValidPayload("evt-outage-no-persist"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var followUp = await client.GetAsync("/events/evt-outage-no-persist");
        Assert.Equal(HttpStatusCode.NotFound, followUp.StatusCode);
    }

    // RES-7: GET /events/{id} and GET /events?account=... are served entirely from the Gateway's
    // own local data and must keep working during an Account Service outage. Seeds a record while
    // the Account Service is reachable, then reopens the same database against an always-failing
    // handler and confirms both read endpoints still return the seeded data unchanged.
    [Fact]
    public async Task GetEventById_And_ListByAccount_UnaffectedByAccountServiceOutage()
    {
        using (var seedFactory = CreateFactory())
        using (var seedClient = seedFactory.CreateClient())
        {
            var seedResponse = await seedClient.PostAsJsonAsync("/events", ValidPayload("evt-read-during-outage"));
            Assert.Equal(HttpStatusCode.Created, seedResponse.StatusCode);
        }

        using var factory = CreateFactory(new FlakyAccountServiceHandler(failuresBeforeSuccess: int.MaxValue));
        using var client = factory.CreateClient();

        var byIdResponse = await client.GetAsync("/events/evt-read-during-outage");
        Assert.Equal(HttpStatusCode.OK, byIdResponse.StatusCode);
        var byIdBody = await byIdResponse.Content.ReadFromJsonAsync<EventResponseDto>();
        Assert.Equal("evt-read-during-outage", byIdBody!.EventId);

        var byAccountResponse = await client.GetAsync("/events?account=acct-1");
        Assert.Equal(HttpStatusCode.OK, byAccountResponse.StatusCode);
        var byAccountBody = await byAccountResponse.Content.ReadFromJsonAsync<List<EventResponseDto>>();
        Assert.Contains(byAccountBody!, e => e.EventId == "evt-read-during-outage");
    }

    // Issues enough POST /events calls to exceed the circuit breaker's minimum throughput (4)
    // at a 100% failure ratio, tripping it open. Shared by RES-3 and RES-4, which both need the
    // circuit open before exercising what happens next.
    private static async Task TripCircuitOpenAsync(HttpClient client, string eventIdPrefix)
    {
        for (var i = 0; i < 4; i++)
        {
            await client.PostAsJsonAsync("/events", ValidPayload($"{eventIdPrefix}-{i}"));
        }
    }

    private static Task<HttpResponseMessage> PostWithTimestamp(HttpClient client, string eventId, string eventTimestamp) =>
        client.PostAsJsonAsync("/events", new
        {
            eventId,
            accountId = "acct-1",
            type = "CREDIT",
            amount = 100m,
            currency = "USD",
            eventTimestamp
        });

    // Simulates a hung Account Service: never returns within any reasonable time on its own —
    // only the resilience pipeline's timeout (via the CancellationToken passed to Task.Delay)
    // cuts it short. Used to prove RES-2 (a hung call is bounded, not indefinite).
    private sealed class HangingAccountServiceHandler(TimeSpan delay) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    // Fails the first `failuresBeforeSuccess` calls with `failureStatus`, then succeeds on every
    // call after that. CallCount lets tests assert not just the final response, but whether the
    // pipeline actually attempted the network call it's expected to (or not, when the circuit is
    // open) — the assertion that actually proves retry/circuit-breaker behavior, not just its
    // externally-visible side effect.
    private sealed class FlakyAccountServiceHandler(int failuresBeforeSuccess, HttpStatusCode failureStatus = HttpStatusCode.InternalServerError) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        // Mutable (not just constructor-set) so a test can change failure behavior mid-run —
        // e.g. RES-4 needs this handler to always fail while tripping the circuit open, then
        // start succeeding for the half-open trial call, without needing a second handler class.
        public int FailuresBeforeSuccess { get; set; } = failuresBeforeSuccess;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var status = CallCount <= FailuresBeforeSuccess ? failureStatus : HttpStatusCode.Created;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed record EventResponseDto(string EventId, string AccountId, string Type, decimal Amount, string Currency, DateTimeOffset EventTimestamp, object? Metadata, DateTimeOffset ReceivedAt);

    private sealed record ErrorResponseDto(string Error, string Message, object? Details);
}
