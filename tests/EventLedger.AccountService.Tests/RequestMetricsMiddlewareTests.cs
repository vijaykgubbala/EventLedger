using System.Diagnostics.Metrics;
using System.Net;
using EventLedger.AccountService.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventLedger.AccountService.Tests;

public class RequestMetricsMiddlewareTests : IDisposable
{
    private readonly SqliteTempDbFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private sealed record RecordedMeasurement(long Value, string? Endpoint, int? StatusCode);

    private static MeterListener CreateListener(string meterName, List<RecordedMeasurement> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == meterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            string? endpoint = null;
            int? statusCode = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "endpoint")
                {
                    endpoint = tag.Value as string;
                }
                else if (tag.Key == "status_code")
                {
                    statusCode = tag.Value as int?;
                }
            }

            measurements.Add(new RecordedMeasurement(measurement, endpoint, statusCode));
        });

        listener.Start();
        return listener;
    }

    [Fact]
    public async Task GetHealth_RecordsOneRequestCounterMeasurementTaggedWithEndpointAndStatus()
    {
        var measurements = new List<RecordedMeasurement>();
        var listener = CreateListener("EventLedger.AccountService", measurements);

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await client.GetAsync("/health");

        listener.Dispose();

        var measurement = Assert.Single(measurements);
        Assert.Equal(1, measurement.Value);
        Assert.Equal("health", measurement.Endpoint);
        Assert.Equal(200, measurement.StatusCode);
    }

    [Fact]
    public async Task GetAccountBalance_RecordsMeasurementTaggedWithRouteTemplateNotRawPath()
    {
        // accounts/{accountId}/balance has a route parameter — this is what actually exercises
        // the Q6 cardinality decision (route template vs. raw path).
        var measurements = new List<RecordedMeasurement>();
        var listener = CreateListener("EventLedger.AccountService", measurements);

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await client.GetAsync("/accounts/acct-metrics-test/balance");

        listener.Dispose();

        var measurement = Assert.Single(measurements);
        Assert.Equal(1, measurement.Value);
        Assert.DoesNotContain("acct-metrics-test", measurement.Endpoint);
        Assert.Equal(200, measurement.StatusCode);
    }

    [Fact]
    public async Task RequestThatThrowsUnhandledException_StillRecordsMeasurement()
    {
        var measurements = new List<RecordedMeasurement>();
        var listener = CreateListener("EventLedger.AccountService", measurements);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AccountDbContext>>();
                services.AddDbContext<AccountDbContext>(opt => opt.UseSqlite(_fixture.ConnectionString));
            }));

        // Forces host startup (creating the correct schema via EnsureAccountServiceDatabaseCreated()),
        // then corrupts it to force a genuine unhandled SqliteException on the next query —
        // proving the middleware still records the measurement instead of silently dropping it.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE transactions");
        }

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/accounts/acct-anything/balance");

        listener.Dispose();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var measurement = Assert.Single(measurements);
        Assert.Equal(1, measurement.Value);
        Assert.Equal(500, measurement.StatusCode);
    }
}
