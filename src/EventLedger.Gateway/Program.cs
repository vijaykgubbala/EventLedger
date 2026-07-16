using EventLedger.Gateway.Infrastructure;
using EventLedger.Gateway.Middleware;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ServiceName", "EventGateway")
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog();
builder.AddGatewayInfrastructure();
builder.Services.AddControllers();

var app = builder.Build();

app.UseTraceLogging();
app.MapControllers();

app.Run();
