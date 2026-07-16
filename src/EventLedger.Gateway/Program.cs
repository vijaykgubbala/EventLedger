using EventLedger.Gateway.Infrastructure;
using EventLedger.Gateway.Middleware;
using Serilog;

const string serviceName = "EventGateway";

ServiceCollectionExtensions.BootstrapLogging(serviceName);

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.AddGatewayInfrastructure(serviceName);

    var app = builder.Build();

    app.EnsureGatewayDatabaseCreated();

    app.UseTraceLogging();
    app.UseRequestMetrics();
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
