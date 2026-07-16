using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Routing;

namespace EventLedger.Gateway.Middleware;

public static class RequestMetricsMiddleware
{
    public const string MeterName = "EventLedger.Gateway";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> RequestCounter = Meter.CreateCounter<long>("http.requests.total");

    public static IApplicationBuilder UseRequestMetrics(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            await next();

            var endpoint = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unknown";
            RequestCounter.Add(1,
                new KeyValuePair<string, object?>("endpoint", endpoint),
                new KeyValuePair<string, object?>("status_code", context.Response.StatusCode));
        });
    }
}
