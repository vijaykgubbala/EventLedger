using Microsoft.EntityFrameworkCore;
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

    public static WebApplicationBuilder AddGatewayInfrastructure(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog();
        builder.Services.AddControllers();
        builder.Services.AddDbContext<GatewayDbContext>(opt =>
            opt.UseSqlite(builder.Configuration.GetConnectionString("Gateway")));

        return builder;
    }

    public static WebApplication EnsureGatewayDatabaseCreated(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<GatewayDbContext>().Database.EnsureCreated();

        return app;
    }
}
