using EventLedger.AccountService.Domain;
using EventLedger.AccountService.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.AccountService.Tests;

public class AccountDbContextTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AccountDbContext _db;

    public AccountDbContextTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"account-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AccountDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _db = new AccountDbContext(options);
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
}
