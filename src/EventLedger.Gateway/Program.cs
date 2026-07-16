using EventLedger.Gateway.Infrastructure;
using EventLedger.Gateway.Middleware;
using Serilog;

ServiceCollectionExtensions.BootstrapLogging("EventGateway");

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.AddGatewayInfrastructure();

    var app = builder.Build();

    app.UseTraceLogging();
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "EventGateway terminated unexpectedly during startup");
}
finally
{
    Log.CloseAndFlush();
}
