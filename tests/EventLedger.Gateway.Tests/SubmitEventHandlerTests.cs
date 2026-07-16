using System.Net;
using EventLedger.Gateway.Application;
using EventLedger.Gateway.Domain;
using EventLedger.Gateway.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.Gateway.Tests;

public class SubmitEventHandlerTests : IDisposable
{
    private readonly string _dbPath;

    public SubmitEventHandlerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"submit-event-test-{Guid.NewGuid():N}.db");
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private GatewayDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new GatewayDbContext(options);
    }

    private static SubmitEventHandler CreateHandler(GatewayDbContext db, HttpStatusCode accountServiceStatus = HttpStatusCode.Created) =>
        new(db, new StubHttpClientFactory(accountServiceStatus), NullLogger<SubmitEventHandler>.Instance);

    private static SubmitEventHandler CreateHandler(GatewayDbContext db, CountingStubHandler handler) =>
        new(db, new StubHttpClientFactory(handler), NullLogger<SubmitEventHandler>.Instance);

    private static DateTimeOffset Timestamp => DateTimeOffset.Parse("2026-05-15T14:02:11Z");

    [Fact]
    public async Task SubmitAsync_ValidNewEvent_CallsAccountServiceInsertsLocallyReturnsNewRecord()
    {
        using var db = CreateContext();
        var handler = CreateHandler(db);

        var result = await handler.SubmitAsync("evt-1", "acct-1", TransactionType.Credit, 100m, "USD", Timestamp, null);

        Assert.Equal(SubmitEventOutcome.Created, result.Outcome);
        Assert.NotNull(result.Event);
        Assert.Equal(1, await db.Events.CountAsync());
    }

    [Fact]
    public async Task SubmitAsync_EventIdAlreadyStoredLocally_ReturnsExistingRecordAccountServiceNotCalled()
    {
        using var seedDb = CreateContext();
        await CreateHandler(seedDb).SubmitAsync("evt-2", "acct-1", TransactionType.Credit, 100m, "USD", Timestamp, null);

        var callCounter = new CountingStubHandler(HttpStatusCode.Created);
        using var db = CreateContext();
        var handler = CreateHandler(db, callCounter);

        var result = await handler.SubmitAsync("evt-2", "acct-1", TransactionType.Credit, 100m, "USD", Timestamp, null);

        Assert.Equal(SubmitEventOutcome.Duplicate, result.Outcome);
        Assert.Equal(0, callCounter.CallCount);
        Assert.Equal(1, await db.Events.CountAsync(e => e.EventId == "evt-2"));
    }

    [Fact]
    public async Task SubmitAsync_ConcurrentCallsSameNewEventId_ExactlyOneRowLoserGetsWinnersRecord()
    {
        using var db1 = CreateContext();
        using var db2 = CreateContext();

        var task1 = CreateHandler(db1).SubmitAsync("evt-3", "acct-1", TransactionType.Credit, 100m, "USD", Timestamp, null);
        var task2 = CreateHandler(db2).SubmitAsync("evt-3", "acct-1", TransactionType.Credit, 100m, "USD", Timestamp, null);

        var results = await Task.WhenAll(task1, task2);

        using var verifyDb = CreateContext();
        Assert.Equal(1, await verifyDb.Events.CountAsync(e => e.EventId == "evt-3"));
        Assert.Equal(results[0].Event!.Id, results[1].Event!.Id);
    }

    [Fact]
    public async Task SubmitAsync_ResubmittedWithDifferentPayload_ReturnsOriginalUnchanged()
    {
        using var seedDb = CreateContext();
        await CreateHandler(seedDb).SubmitAsync("evt-4", "acct-1", TransactionType.Credit, 100m, "USD", Timestamp, null);

        using var db = CreateContext();
        var handler = CreateHandler(db);

        var result = await handler.SubmitAsync("evt-4", "acct-1", TransactionType.Debit, 999m, "USD", Timestamp, null);

        Assert.Equal(SubmitEventOutcome.Duplicate, result.Outcome);
        Assert.Equal(TransactionType.Credit, result.Event!.Type);
        Assert.Equal(100m, result.Event.Amount);
    }

    [Fact]
    public async Task SubmitAsync_OutOfOrderEventTimestampsAcrossMultipleEvents_AllApplySuccessfully()
    {
        using var db = CreateContext();
        var handler = CreateHandler(db);

        var later = Timestamp;
        var earlier = Timestamp.AddDays(-1);

        var first = await handler.SubmitAsync("evt-5a", "acct-1", TransactionType.Credit, 200m, "USD", later, null);
        var second = await handler.SubmitAsync("evt-5b", "acct-1", TransactionType.Debit, 50m, "USD", earlier, null);

        Assert.Equal(SubmitEventOutcome.Created, first.Outcome);
        Assert.Equal(SubmitEventOutcome.Created, second.Outcome);
        Assert.Equal(2, await db.Events.CountAsync(e => e.AccountId == "acct-1"));
    }

    [Fact]
    public async Task SubmitAsync_AccountServiceUnavailable_ReturnsUnavailableOutcomeNothingPersisted()
    {
        using var db = CreateContext();
        var handler = CreateHandler(db, HttpStatusCode.ServiceUnavailable);

        var result = await handler.SubmitAsync("evt-6", "acct-1", TransactionType.Credit, 100m, "USD", Timestamp, null);

        Assert.Equal(SubmitEventOutcome.AccountServiceUnavailable, result.Outcome);
        Assert.Equal(0, await db.Events.CountAsync());
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpStatusCode status) : this(new CountingStubHandler(status))
        {
        }

        public StubHttpClientFactory(CountingStubHandler handler)
        {
            _client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5199") };
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class CountingStubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
