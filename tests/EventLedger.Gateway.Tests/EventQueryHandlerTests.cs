using EventLedger.Gateway.Application;
using EventLedger.Gateway.Domain;
using EventLedger.Gateway.Infrastructure;

namespace EventLedger.Gateway.Tests;

public class EventQueryHandlerTests : IDisposable
{
    private readonly SqliteTempDbFixture _fixture = new();
    private readonly GatewayDbContext _db;

    public EventQueryHandlerTests()
    {
        _db = _fixture.CreateContext();
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _fixture.Dispose();
    }

    private static EventRecord NewEvent(string eventId, string accountId, DateTimeOffset eventTimestamp) => new()
    {
        EventId = eventId,
        AccountId = accountId,
        Type = TransactionType.Credit,
        Amount = 100m,
        Currency = "USD",
        EventTimestamp = eventTimestamp,
        ReceivedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task GetByIdAsync_ExistingEventId_ReturnsRecord()
    {
        _db.Events.Add(NewEvent("evt-1", "acct-1", DateTimeOffset.UtcNow));
        await _db.SaveChangesAsync();

        var handler = new EventQueryHandler(_db);
        var result = await handler.GetByIdAsync("evt-1");

        Assert.NotNull(result);
        Assert.Equal("evt-1", result!.EventId);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentEventId_ReturnsNull()
    {
        var handler = new EventQueryHandler(_db);

        var result = await handler.GetByIdAsync("evt-missing");

        Assert.Null(result);
    }

    [Fact]
    public async Task ListByAccountAsync_MultipleEventsOutOfArrivalOrder_ReturnsOrderedByEventTimestampAscending()
    {
        var now = DateTimeOffset.UtcNow;
        _db.Events.AddRange(
            NewEvent("evt-later", "acct-1", now),
            NewEvent("evt-earliest", "acct-1", now.AddDays(-2)),
            NewEvent("evt-middle", "acct-1", now.AddDays(-1)),
            NewEvent("evt-other-account", "acct-2", now.AddDays(-5)));
        await _db.SaveChangesAsync();

        var handler = new EventQueryHandler(_db);
        var results = await handler.ListByAccountAsync("acct-1");

        Assert.Equal(["evt-earliest", "evt-middle", "evt-later"], results.Select(e => e.EventId));
    }
}
