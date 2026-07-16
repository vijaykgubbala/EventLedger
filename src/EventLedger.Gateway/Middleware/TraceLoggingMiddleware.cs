using System.Diagnostics;
using Serilog.Context;

namespace EventLedger.Gateway.Middleware;

public static class TraceLoggingMiddleware
{
    public static IApplicationBuilder UseTraceLogging(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var traceId = Activity.Current?.TraceId.ToString();
            using (LogContext.PushProperty("TraceId", traceId))
            {
                await next();
            }
        });
    }
}
