using EventLedger.AccountService.Application;
using EventLedger.AccountService.Domain;
using EventLedger.AccountService.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.AccountService.Tests;

public class AccountDetailsHandlerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AccountDbContext _db;

    public AccountDetailsHandlerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"account-details-test-{Guid.NewGuid():N}.db");
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

    [Fact]
    public async Task GetDetailsAsync_ReturnsAccountIdAndFullTransactionList()
    {
        _db.Transactions.AddRange(
            new TransactionRecord { EventId = "evt-1", AccountId = "acct-1", Type = TransactionType.Credit, Amount = 100m, AppliedAt = DateTimeOffset.UtcNow },
            new TransactionRecord { EventId = "evt-2", AccountId = "acct-1", Type = TransactionType.Debit, Amount = 40m, AppliedAt = DateTimeOffset.UtcNow },
            new TransactionRecord { EventId = "evt-3", AccountId = "acct-other", Type = TransactionType.Credit, Amount = 500m, AppliedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();

        var handler = new AccountDetailsHandler(_db, new BalanceQueryHandler(_db));
        var details = await handler.GetDetailsAsync("acct-1");

        Assert.Equal("acct-1", details.AccountId);
        Assert.Equal(2, details.Transactions.Count);
        Assert.All(details.Transactions, t => Assert.Equal("acct-1", t.AccountId));
        Assert.Equal(60m, details.Balance);
    }
}
