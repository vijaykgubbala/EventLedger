using EventLedger.AccountService.Domain;
using EventLedger.AccountService.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.AccountService.Tests;

public class AccountDbContextTests : IDisposable
{
    private readonly SqliteTempDbFixture _fixture = new();
    private readonly AccountDbContext _db;

    public AccountDbContextTests()
    {
        _db = _fixture.CreateContext();
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _fixture.Dispose();
    }

    private static TransactionRecord NewTransaction(string eventId, decimal amount = 100m) => new()
    {
        EventId = eventId,
        AccountId = "acct-1",
        Type = TransactionType.Credit,
        Amount = amount,
        AppliedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Insert_DuplicateEventId_ThrowsDbUpdateException()
    {
        _db.Transactions.Add(NewTransaction("evt-1"));
        await _db.SaveChangesAsync();

        _db.Transactions.Add(NewTransaction("evt-1"));

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Insert_NonPositiveAmount_ThrowsDbUpdateException()
    {
        _db.Transactions.Add(NewTransaction("evt-2", amount: 0m));

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Insert_UnmappedTransactionTypeValue_ThrowsBeforeReachingDatabase()
    {
        var invalid = NewTransaction("evt-invalid-type");
        invalid.Type = (TransactionType)99;
        _db.Transactions.Add(invalid);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
        Assert.IsType<ArgumentOutOfRangeException>(ex.InnerException);
    }

    [Fact]
    public async Task Schema_UsesSnakeCaseColumnNames()
    {
        var connection = _db.Database.GetDbConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'transactions'";
        var schema = (string)(await command.ExecuteScalarAsync())!;

        Assert.Contains("\"event_id\"", schema);
        Assert.Contains("\"account_id\"", schema);
        Assert.Contains("\"applied_at\"", schema);
        Assert.DoesNotContain("\"EventId\"", schema);
    }
}
