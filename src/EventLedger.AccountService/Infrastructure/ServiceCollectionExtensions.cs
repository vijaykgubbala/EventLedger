using Microsoft.EntityFrameworkCore;
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

    public static WebApplicationBuilder AddAccountServiceInfrastructure(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog();
        builder.Services.AddControllers();
        builder.Services.AddDbContext<AccountDbContext>(opt =>
            opt.UseSqlite(builder.Configuration.GetConnectionString("AccountService")));

        return builder;
    }

    public static WebApplication EnsureAccountServiceDatabaseCreated(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccountDbContext>().Database.EnsureCreated();

        return app;
    }
}
