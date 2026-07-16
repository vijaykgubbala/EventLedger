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

    private WebApplicationFactory<Program> CreateFactory(HttpStatusCode accountServiceStatus = HttpStatusCode.Created) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<GatewayDbContext>>();
                services.AddDbContext<GatewayDbContext>(opt => opt.UseSqlite(_fixture.ConnectionString));

                services.AddHttpClient("AccountService")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubAccountServiceHandler(accountServiceStatus));
            });
        });

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

    // RES-2: a hung Account Service call is bounded by the resilience pipeline's total timeout,
    // not indefinite. The handler hangs for 30s — far longer than the pipeline could ever take
    // (2s timeout x up to 3 attempts + 200ms retry delays ~= 6.4s worst case) — so a response
    // arriving well under that hang duration proves the timeout, not the hang, determined it.
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
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        Assert.Equal("account_service_unavailable", body!.Error);
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

    private sealed class StubAccountServiceHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    // Simulates a hung Account Service: never returns within any reasonable time on its own —
    // only the resilience pipeline's timeout (via the CancellationToken passed to Task.Delay)
    // cuts it short. Used to prove RES-2 (a hung call is bounded, not indefinite).
    private sealed class HangingAccountServiceHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var status = CallCount <= failuresBeforeSuccess ? failureStatus : HttpStatusCode.Created;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed record EventResponseDto(string EventId, string AccountId, string Type, decimal Amount, string Currency, DateTimeOffset EventTimestamp, object? Metadata, DateTimeOffset ReceivedAt);

    private sealed record ErrorResponseDto(string Error, string Message, object? Details);
}
