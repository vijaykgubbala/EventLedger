using EventLedger.AccountService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddAccountServiceInfrastructure();
builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();
