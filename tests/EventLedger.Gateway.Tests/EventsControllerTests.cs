using System.Net;
using System.Net.Http.Json;
using EventLedger.Gateway.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventLedger.Gateway.Tests;

public class EventsControllerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"events-controller-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private WebApplicationFactory<Program> CreateFactory(HttpStatusCode accountServiceStatus = HttpStatusCode.Created) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<GatewayDbContext>>();
                services.AddDbContext<GatewayDbContext>(opt => opt.UseSqlite($"Data Source={_dbPath}"));

                services.AddHttpClient("AccountService")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubAccountServiceHandler(accountServiceStatus));
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

    private sealed record EventResponseDto(string EventId, string AccountId, string Type, decimal Amount, string Currency, DateTimeOffset EventTimestamp, object? Metadata, DateTimeOffset ReceivedAt);

    private sealed record ErrorResponseDto(string Error, string Message, object? Details);
}
