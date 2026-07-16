using EventLedger.AccountService.Application;
using EventLedger.AccountService.Middleware;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace EventLedger.AccountService.Infrastructure;

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

    public static WebApplicationBuilder AddAccountServiceInfrastructure(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Host.UseSerilog();
        builder.Services.AddControllers();
        builder.Services.AddDbContext<AccountDbContext>(opt =>
            opt.UseSqlite(builder.Configuration.GetConnectionString("AccountService")));
        builder.Services.AddScoped<ApplyTransactionHandler>();
        builder.Services.AddScoped<BalanceQueryHandler>();
        builder.Services.AddScoped<AccountDetailsHandler>();
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation())
            .WithMetrics(metrics => metrics.AddMeter(RequestMetricsMiddleware.MeterName));

        return builder;
    }

    public static WebApplication EnsureAccountServiceDatabaseCreated(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccountDbContext>().Database.EnsureCreated();

        return app;
    }
}
