using EventLedger.Gateway.Application;
using EventLedger.Gateway.Middleware;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
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
            client.BaseAddress = new Uri(builder.Configuration["AccountService:BaseUrl"]!));
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
}
