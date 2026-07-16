extern alias AccountServiceAssembly;

using System.Net;
using System.Net.Http.Json;
using EventLedger.Gateway.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AccountServiceDbContext = AccountServiceAssembly::EventLedger.AccountService.Infrastructure.AccountDbContext;
using AccountServiceProgram = AccountServiceAssembly::Program;

namespace EventLedger.Gateway.Tests;

public class GatewayToAccountServiceFullFlowTests : IDisposable
{
    // AccountService.Tests' SqliteTempDbFixture lives in a different test assembly than this
    // project's src-only extern alias covers, so the Account Service side of this one-off
    // dual-service test keeps its own minimal temp-file handling rather than reusing it.
    private readonly SqliteTempDbFixture _gatewayFixture = new();
    private readonly string _accountDbPath = Path.Combine(Path.GetTempPath(), $"account-e2e-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        _gatewayFixture.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_accountDbPath))
        {
            File.Delete(_accountDbPath);
        }
    }

    [Fact]
    public async Task PostEvents_FullFlowThroughRealAccountService_AppliesTransactionAndPersistsInBothServices()
    {
        await using var accountServiceFactory = new WebApplicationFactory<AccountServiceProgram>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AccountServiceDbContext>>();
                services.AddDbContext<AccountServiceDbContext>(opt => opt.UseSqlite($"Data Source={_accountDbPath}"));
            });
        });

        await using var gatewayFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<GatewayDbContext>>();
                services.AddDbContext<GatewayDbContext>(opt => opt.UseSqlite(_gatewayFixture.ConnectionString));

                services.AddHttpClient("AccountService")
                    .ConfigurePrimaryHttpMessageHandler(() => accountServiceFactory.Server.CreateHandler());
            });
        });

        using var client = gatewayFactory.CreateClient();

        var response = await client.PostAsJsonAsync("/events", new
        {
            eventId = "evt-e2e-1",
            accountId = "acct-e2e-1",
            type = "CREDIT",
            amount = 150m,
            currency = "USD",
            eventTimestamp = "2026-05-15T14:02:11Z"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var accountServiceScope = accountServiceFactory.Services.CreateScope();
        var accountDb = accountServiceScope.ServiceProvider.GetRequiredService<AccountServiceDbContext>();
        var applied = await accountDb.Transactions.SingleAsync(t => t.EventId == "evt-e2e-1");
        Assert.Equal("acct-e2e-1", applied.AccountId);
        Assert.Equal(150m, applied.Amount);

        using var gatewayScope = gatewayFactory.Services.CreateScope();
        var gatewayDb = gatewayScope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var stored = await gatewayDb.Events.SingleAsync(e => e.EventId == "evt-e2e-1");
        Assert.Equal("acct-e2e-1", stored.AccountId);
    }
}
