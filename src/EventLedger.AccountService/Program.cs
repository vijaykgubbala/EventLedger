using EventLedger.AccountService.Infrastructure;
using EventLedger.AccountService.Middleware;
using Serilog;

ServiceCollectionExtensions.BootstrapLogging("AccountService");

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.AddAccountServiceInfrastructure();

    var app = builder.Build();

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
