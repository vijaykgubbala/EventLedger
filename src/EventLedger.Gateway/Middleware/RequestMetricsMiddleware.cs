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
            try
            {
                await next();
                RecordMeasurement(context, context.Response.StatusCode);
            }
            catch
            {
                // context.Response.StatusCode isn't reliably set to its final value yet at this
                // point — that happens in the hosting layer's outer catch, above this middleware.
                // An unhandled exception always ends in a 500 (unless the response has already
                // started, in which case the connection is aborted rather than completed), so
                // record it explicitly here and rethrow to preserve normal exception propagation.
                RecordMeasurement(context, StatusCodes.Status500InternalServerError);
                throw;
            }
        });
    }

    private static void RecordMeasurement(HttpContext context, int statusCode)
    {
        var endpoint = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unknown";
        RequestCounter.Add(1,
            new KeyValuePair<string, object?>("endpoint", endpoint),
            new KeyValuePair<string, object?>("status_code", statusCode));
    }
}
