using EventLedger.Gateway.Domain;
using EventLedger.Gateway.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.Gateway.Tests;

public class GatewayDbContextTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GatewayDbContext _db;

    public GatewayDbContextTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gateway-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _db = new GatewayDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static EventRecord NewEvent(string eventId, decimal amount = 100m) => new()
    {
        EventId = eventId,
        AccountId = "acct-1",
        Type = TransactionType.Credit,
        Amount = amount,
        Currency = "USD",
        EventTimestamp = DateTimeOffset.UtcNow,
        ReceivedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Insert_DuplicateEventId_ThrowsDbUpdateException()
    {
        _db.Events.Add(NewEvent("evt-1"));
        await _db.SaveChangesAsync();

        _db.Events.Add(NewEvent("evt-1"));

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Insert_NonPositiveAmount_ThrowsDbUpdateException()
    {
        _db.Events.Add(NewEvent("evt-2", amount: 0m));

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Insert_UnmappedTransactionTypeValue_ThrowsBeforeReachingDatabase()
    {
        var invalid = NewEvent("evt-invalid-type");
        invalid.Type = (TransactionType)99;
        _db.Events.Add(invalid);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
        Assert.IsType<ArgumentOutOfRangeException>(ex.InnerException);
    }

    [Fact]
    public async Task Schema_UsesSnakeCaseColumnNames()
    {
        var connection = _db.Database.GetDbConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'events'";
        var schema = (string)(await command.ExecuteScalarAsync())!;

        Assert.Contains("\"event_id\"", schema);
        Assert.Contains("\"account_id\"", schema);
        Assert.Contains("\"event_timestamp\"", schema);
        Assert.Contains("\"metadata_json\"", schema);
        Assert.Contains("\"received_at\"", schema);
        Assert.DoesNotContain("\"EventId\"", schema);
    }
}
