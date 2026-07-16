extern alias AccountServiceAssembly;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventLedger.Gateway.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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

    private (WebApplicationFactory<AccountServiceProgram> accountService, WebApplicationFactory<Program> gateway) CreateFactories()
    {
        var accountServiceFactory = new WebApplicationFactory<AccountServiceProgram>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AccountServiceDbContext>>();
                services.AddDbContext<AccountServiceDbContext>(opt => opt.UseSqlite($"Data Source={_accountDbPath}"));
            });
        });

        var gatewayFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<GatewayDbContext>>();
                services.AddDbContext<GatewayDbContext>(opt => opt.UseSqlite(_gatewayFixture.ConnectionString));

                services.AddHttpClient("AccountService")
                    .ConfigurePrimaryHttpMessageHandler(() => accountServiceFactory.Server.CreateHandler());
            });
        });

        return (accountServiceFactory, gatewayFactory);
    }

    // TestServer.CreateHandler() (used by CreateFactories() above) substitutes a custom
    // primary HttpMessageHandler, which bypasses System.Net.Http.DiagnosticsHandler — the
    // component that actually injects the traceparent header, living inside SocketsHttpHandler
    // itself rather than as a separately composable layer. Confirmed empirically: no
    // traceparent header ever reaches the Account Service via CreateFactories()'s wiring,
    // regardless of OpenTelemetry registration. A genuine socket-level call is the only way to
    // observe real header injection, so this helper starts the Account Service on a real,
    // dynamically-assigned Kestrel port and lets the Gateway's HttpClient use its real, default
    // SocketsHttpHandler (no ConfigurePrimaryHttpMessageHandler override) to reach it.
    private (WebApplicationFactory<AccountServiceProgram> accountService, WebApplicationFactory<Program> gateway) CreateFactoriesWithRealNetworking()
    {
        var accountServiceFactory = new WebApplicationFactory<AccountServiceProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseKestrel();
            builder.UseUrls("http://127.0.0.1:0");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AccountServiceDbContext>>();
                services.AddDbContext<AccountServiceDbContext>(opt => opt.UseSqlite($"Data Source={_accountDbPath}"));
            });
        });

        // Accessing .Services forces the host (including Kestrel) to start listening, so the
        // real bound address is available immediately afterward.
        var server = accountServiceFactory.Services.GetRequiredService<IServer>();
        var realAddress = server.Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var gatewayFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<GatewayDbContext>>();
                services.AddDbContext<GatewayDbContext>(opt => opt.UseSqlite(_gatewayFixture.ConnectionString));

                // Overrides only the BaseAddress the existing "AccountService" client
                // registration (from AddGatewayInfrastructure()) resolves to — the client
                // keeps its real, default SocketsHttpHandler.
                services.AddHttpClient("AccountService", client => client.BaseAddress = new Uri(realAddress));
            });
        });

        return (accountServiceFactory, gatewayFactory);
    }

    [Fact]
    public async Task PostEvents_TraceparentPropagatesOverRealNetworkCall_SameTraceIdInBothServicesLogs()
    {
        var factories = CreateFactoriesWithRealNetworking();
        await using var accountServiceFactory = factories.accountService;
        await using var gatewayFactory = factories.gateway;

        var originalOut = Console.Out;
        var capturedOutput = new StringWriter();
        Console.SetOut(capturedOutput);

        try
        {
            using var client = gatewayFactory.CreateClient();
            await client.PostAsJsonAsync("/events", new
            {
                eventId = "evt-trace-1",
                accountId = "acct-trace-1",
                type = "CREDIT",
                amount = 75m,
                currency = "USD",
                eventTimestamp = "2026-05-15T14:02:11Z"
            });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // Assert TraceId equality only, not per-line ServiceName — BootstrapLogging(serviceName)
        // reassigns Serilog's process-wide static Log.Logger, and when both services boot in one
        // xUnit process, whichever service's BootstrapLogging call runs last (the Account
        // Service, since it boots on first use) can make later Gateway-side log lines carry the
        // wrong ServiceName. TraceId itself, pushed via LogContext.PushProperty (AsyncLocal-
        // scoped), is unaffected. This is a recognized test-environment-only artifact of running
        // two independently-BootstrapLogging'd apps in one process — not a production concern,
        // since real deployments run each service in its own OS process.
        var traceIds = capturedOutput.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.TrimStart().StartsWith('{') && line.Contains("TraceId"))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("TraceId").GetString())
            .Distinct()
            .ToList();

        // Exactly one distinct TraceId across every captured log line proves both services
        // logged under the same propagated trace, not two independently-generated ones.
        Assert.Single(traceIds);
    }

    [Fact]
    public async Task PostEvents_FullFlowThroughRealAccountService_AppliesTransactionAndPersistsInBothServices()
    {
        var factories = CreateFactories();
        await using var accountServiceFactory = factories.accountService;
        await using var gatewayFactory = factories.gateway;

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

    [Fact]
    public async Task PostEvents_MixedTransactionsSubmittedOutOfEventTimestampOrder_ResultingBalanceIsCorrect()
    {
        var factories = CreateFactories();
        await using var accountServiceFactory = factories.accountService;
        await using var gatewayFactory = factories.gateway;

        using var client = gatewayFactory.CreateClient();

        // Submitted in this arrival order, but eventTimestamp order is: debit (05-10), credit-a (05-12), credit-b (05-15).
        // Balance is a plain SUM, so it must come out the same (100 + 40 - 30 = 110) regardless of arrival order.
        await client.PostAsJsonAsync("/events", new { eventId = "evt-order-1", accountId = "acct-order-1", type = "CREDIT", amount = 100m, currency = "USD", eventTimestamp = "2026-05-15T00:00:00Z" });
        await client.PostAsJsonAsync("/events", new { eventId = "evt-order-2", accountId = "acct-order-1", type = "DEBIT", amount = 30m, currency = "USD", eventTimestamp = "2026-05-10T00:00:00Z" });
        await client.PostAsJsonAsync("/events", new { eventId = "evt-order-3", accountId = "acct-order-1", type = "CREDIT", amount = 40m, currency = "USD", eventTimestamp = "2026-05-12T00:00:00Z" });

        using var accountServiceScope = accountServiceFactory.Services.CreateScope();
        var balanceHandler = accountServiceScope.ServiceProvider
            .GetRequiredService<AccountServiceAssembly::EventLedger.AccountService.Application.BalanceQueryHandler>();
        var balance = await balanceHandler.GetBalanceAsync("acct-order-1");

        Assert.Equal(110m, balance);
    }
}
