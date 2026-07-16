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

        return builder;
    }
}
