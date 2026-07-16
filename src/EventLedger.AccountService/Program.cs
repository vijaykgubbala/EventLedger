using EventLedger.AccountService.Infrastructure;
using EventLedger.AccountService.Middleware;
using Serilog;

const string serviceName = "AccountService";

ServiceCollectionExtensions.BootstrapLogging(serviceName);

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.AddAccountServiceInfrastructure(serviceName);

    var app = builder.Build();

    app.EnsureAccountServiceDatabaseCreated();

    app.UseTraceLogging();
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "AccountService terminated unexpectedly during startup");
}
finally
{
    Log.CloseAndFlush();
}
