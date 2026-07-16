using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventLedger.Gateway.Tests;

public class RequestMetricsMiddlewareTests
{
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
        using var listener = CreateListener("EventLedger.Gateway", measurements);

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await client.GetAsync("/health");

        listener.Dispose();

        var measurement = Assert.Single(measurements);
        Assert.Equal(1, measurement.Value);
        // RoutePattern.RawText never carries a leading slash, regardless of how the attribute
        // route was written ([HttpGet("/health")] here).
        Assert.Equal("health", measurement.Endpoint);
        Assert.Equal(200, measurement.StatusCode);
    }

    [Fact]
    public async Task GetEventById_RecordsMeasurementTaggedWithRouteTemplateNotRawPath()
    {
        // events/{eventId} has a route parameter — this is what actually exercises the Q6
        // cardinality decision (route template vs. raw path), unlike /health which has none.
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateListener("EventLedger.Gateway", measurements);

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await client.GetAsync("/events/evt-does-not-exist");

        listener.Dispose();

        var measurement = Assert.Single(measurements);
        Assert.Equal(1, measurement.Value);
        Assert.DoesNotContain("evt-does-not-exist", measurement.Endpoint);
        Assert.Equal(404, measurement.StatusCode);
    }
}
