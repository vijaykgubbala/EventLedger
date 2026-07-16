extern alias AccountServiceAssembly;

using System.Net;
using System.Net.Http.Json;
using EventLedger.Gateway.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

    private void ConfigureAccountServiceDb(IServiceCollection services)
    {
        services.RemoveAll<DbContextOptions<AccountServiceDbContext>>();
        services.AddDbContext<AccountServiceDbContext>(opt => opt.UseSqlite($"Data Source={_accountDbPath}"));
    }

    private void ConfigureGatewayDb(IServiceCollection services)
    {
        services.RemoveAll<DbContextOptions<GatewayDbContext>>();
        services.AddDbContext<GatewayDbContext>(opt => opt.UseSqlite(_gatewayFixture.ConnectionString));
    }

    private (WebApplicationFactory<AccountServiceProgram> accountService, WebApplicationFactory<Program> gateway) CreateFactories()
    {
        var accountServiceFactory = new WebApplicationFactory<AccountServiceProgram>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(ConfigureAccountServiceDb));

        var gatewayFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ConfigureGatewayDb(services);
                services.AddHttpClient("AccountService")
                    .ConfigurePrimaryHttpMessageHandler(() => accountServiceFactory.Server.CreateHandler());
            }));

        return (accountServiceFactory, gatewayFactory);
    }

    // TestServer.CreateHandler() (used by CreateFactories() above) bypasses the component that
    // actually injects the traceparent header, so this helper uses a real Kestrel listener
    // instead, letting the Gateway's HttpClient keep its real SocketsHttpHandler. Getting
    // WebApplicationFactory to genuinely listen on a real socket needs a fixed port (dynamic-port
    // discovery via IServerAddressesFeature never resolves here), a CreateHost override (UseKestrel()
    // from WithWebHostBuilder is silently overridden by WAF's own later TestServer registration),
    // and a RealHost escape hatch (WAF's own .Server/.Services/.CreateClient() all throw
    // InvalidCastException once IServer is genuinely Kestrel). Full investigation and rationale:
    // docs/patterns/2026-07-15-diagnosticshandler-bypassed-by-custom-httpmessagehandler.md and
    // docs/patterns/2026-07-16-webapplicationfactory-forces-testserver.md.
    private const string AccountServiceTestAddress = "http://127.0.0.1:58734";

    private sealed class RealKestrelWebApplicationFactory<TProgram> : WebApplicationFactory<TProgram> where TProgram : class
    {
        public IHost? RealHost { get; private set; }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureWebHost(webHostBuilder =>
            {
                webHostBuilder.ConfigureServices(services => services.RemoveAll<IServer>());
                webHostBuilder.UseKestrel();
                webHostBuilder.UseUrls(AccountServiceTestAddress);
            });

            var host = builder.Build();
            host.Start();
            RealHost = host;
            return host;
        }
    }

    private (WebApplicationFactory<AccountServiceProgram> accountService, WebApplicationFactory<Program> gateway) CreateFactoriesWithRealNetworking()
    {
        var accountServiceFactory = new RealKestrelWebApplicationFactory<AccountServiceProgram>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(ConfigureAccountServiceDb));

        try
        {
            // Forces CreateHost to run (populating RealHost) and the real Kestrel listener to
            // start. The InvalidCastException below is expected — see the class comment above —
            // and is safe to ignore because RealHost is already populated by the time it's thrown.
            _ = accountServiceFactory.Services;
        }
        catch (InvalidCastException)
        {
            // Expected — WAF's own (TServer) cast against the now-real Kestrel IServer.
        }
        catch
        {
            // A genuine startup failure (e.g. the fixed port is already in use) — the real Kestrel
            // listener may already be bound by this point, so dispose rather than leak the socket.
            accountServiceFactory.Dispose();
            throw;
        }

        var gatewayFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ConfigureGatewayDb(services);
                // Only the BaseAddress changes — the client keeps its real SocketsHttpHandler.
                services.AddHttpClient("AccountService", client => client.BaseAddress = new Uri(AccountServiceTestAddress));
            }));

        return (accountServiceFactory, gatewayFactory);
    }

    [Fact]
    public async Task PostEvents_TraceparentPropagatesOverRealNetworkCall_SameTraceIdInBothServicesLogs()
    {
        var factories = CreateFactoriesWithRealNetworking();
        await using var accountServiceFactory = factories.accountService;
        await using var gatewayFactory = factories.gateway;

        HttpResponseMessage? response = null;
        var output = await ConsoleLogCapture.CaptureAsync(async () =>
        {
            using var client = gatewayFactory.CreateClient();
            response = await client.PostAsJsonAsync("/events", new
            {
                eventId = "evt-trace-1",
                accountId = "acct-trace-1",
                type = "CREDIT",
                amount = 75m,
                currency = "USD",
                eventTimestamp = "2026-05-15T14:02:11Z"
            });
        });

        // Guards against a false positive: without this, a request that never actually reached
        // the Account Service would still produce one distinct TraceId (all from the Gateway's
        // own single request-processing Activity) and the assertions below would pass anyway.
        Assert.True(response!.StatusCode == HttpStatusCode.Created, $"Status: {response.StatusCode}\n---LOG OUTPUT---\n{output}");

        var lines = ConsoleLogCapture.ParseLogLinesWithTraceId(output);

        // At least two captured lines guards against the false-positive case above by requiring
        // more than just the Gateway's own logging — combined with the status-code assertion,
        // this means the Account Service was genuinely reached and logged something too.
        Assert.True(lines.Count >= 2, $"Expected at least 2 TraceId-bearing log lines, got {lines.Count}.");

        // Asserts TraceId equality only, not per-line ServiceName — BootstrapLogging(serviceName)
        // reassigns Serilog's process-wide static Log.Logger, so whichever service boots last in
        // this dual-in-process test can make earlier lines carry the wrong ServiceName. TraceId
        // itself (AsyncLocal-scoped via LogContext) is unaffected — this is a test-environment-
        // only artifact, not a production concern (real deployments are separate OS processes).
        var traceIds = lines.Select(line => line.GetProperty("TraceId").GetString()).Distinct().ToList();

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

    // "Verified" acceptance item for issue #7: if the Gateway crashes after the Account Service
    // confirms a transaction but before the Gateway's own local insert commits, a client retry
    // must not double-apply. Recreates that exact post-crash state directly rather than via fault
    // injection: POSTing straight to the Account Service (bypassing the Gateway) leaves the
    // Account Service confirmed and the Gateway's own Events table empty for this eventId — the
    // same state a crash between "Account Service call succeeded" and "SaveChangesAsync" would
    // leave. The "retry" is then an ordinary POST /events call against the Gateway, which finds no
    // local record and calls the Account Service again; the Account Service's own eventId unique
    // constraint returns its existing confirmation instead of applying twice (relies on that
    // constraint, not an outbox — see architecture/vertical-architecture.md's confirm-before-persist
    // decision).
    [Fact]
    public async Task PostEvents_AccountServiceAlreadyConfirmedBeforeGatewayCrash_RetrySucceedsWithoutDoubleApplying()
    {
        var factories = CreateFactories();
        await using var accountServiceFactory = factories.accountService;
        await using var gatewayFactory = factories.gateway;

        using var accountServiceClient = accountServiceFactory.CreateClient();
        var preCrashResponse = await accountServiceClient.PostAsJsonAsync("/accounts/acct-crash-1/transactions", new
        {
            eventId = "evt-crash-1",
            accountId = "acct-crash-1",
            type = "CREDIT",
            amount = 200m
        });
        Assert.Equal(HttpStatusCode.Created, preCrashResponse.StatusCode);

        using var gatewayClient = gatewayFactory.CreateClient();
        var retryResponse = await gatewayClient.PostAsJsonAsync("/events", new
        {
            eventId = "evt-crash-1",
            accountId = "acct-crash-1",
            type = "CREDIT",
            amount = 200m,
            currency = "USD",
            eventTimestamp = "2026-05-15T14:02:11Z"
        });

        Assert.Equal(HttpStatusCode.Created, retryResponse.StatusCode);

        using var gatewayScope = gatewayFactory.Services.CreateScope();
        var gatewayDb = gatewayScope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        Assert.Equal(1, await gatewayDb.Events.CountAsync(e => e.EventId == "evt-crash-1"));

        using var accountServiceScope = accountServiceFactory.Services.CreateScope();
        var balanceHandler = accountServiceScope.ServiceProvider
            .GetRequiredService<AccountServiceAssembly::EventLedger.AccountService.Application.BalanceQueryHandler>();
        var balance = await balanceHandler.GetBalanceAsync("acct-crash-1");

        // The definitive proof: exactly one application of the 200 credit, not two.
        Assert.Equal(200m, balance);
    }
}
