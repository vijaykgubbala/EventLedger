using EventLedger.Gateway.Application;
using EventLedger.Gateway.Middleware;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Serilog;

namespace EventLedger.Gateway.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static void BootstrapLogging(string serviceName)
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("ServiceName", serviceName)
            .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
            .CreateLogger();
    }

    public static WebApplicationBuilder AddGatewayInfrastructure(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Host.UseSerilog();
        builder.Services.AddControllers();
        builder.Services.AddDbContext<GatewayDbContext>(opt =>
            opt.UseSqlite(builder.Configuration.GetConnectionString("Gateway")));
        builder.Services.AddHttpClient("AccountService", client =>
                client.BaseAddress = new Uri(builder.Configuration["AccountService:BaseUrl"]!))
            .AddResilienceHandler("account-service", pipeline => pipeline
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(10),
                    MinimumThroughput = 4,
                    BreakDuration = TimeSpan.FromSeconds(5),
                    ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome))
                })
                .AddTimeout(TimeSpan.FromSeconds(2))
                .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
                {
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.FromMilliseconds(200),
                    BackoffType = DelayBackoffType.Constant,
                    ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome))
                }));
        builder.Services.AddScoped<EventValidator>();
        builder.Services.AddScoped<SubmitEventHandler>();
        builder.Services.AddScoped<EventQueryHandler>();
        builder.Services.AddScoped<HealthCheckHandler>();
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics.AddMeter(RequestMetricsMiddleware.MeterName));

        return builder;
    }

    public static WebApplication EnsureGatewayDatabaseCreated(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<GatewayDbContext>().Database.EnsureCreated();

        return app;
    }

    // Shared by both the circuit breaker and retry strategies so "what counts as a failure"
    // stays consistent between them: a 400 (or any other non-5xx status) is deterministic —
    // retrying it can't help and it shouldn't count against the circuit's failure ratio either.
    private static bool IsTransientFailure(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is not null || (outcome.Result is { } response && (int)response.StatusCode >= 500);
}
