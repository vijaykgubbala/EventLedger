using EventLedger.Gateway.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddGatewayInfrastructure();
builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();
