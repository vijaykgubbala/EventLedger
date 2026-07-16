using EventLedger.AccountService.Application;
using EventLedger.AccountService.Domain;
using EventLedger.AccountService.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.AccountService.Tests;

public class BalanceQueryHandlerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AccountDbContext _db;

    public BalanceQueryHandlerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"balance-test-{Guid.NewGuid():N}.db");
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

    private static TransactionRecord NewTransaction(string accountId, TransactionType type, decimal amount) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        AccountId = accountId,
        Type = type,
        Amount = amount,
        AppliedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task GetBalanceAsync_MixedCreditsAndDebits_ReturnsSumOfCreditsMinusSumOfDebits()
    {
        _db.Transactions.AddRange(
            NewTransaction("acct-1", TransactionType.Credit, 300m),
            NewTransaction("acct-1", TransactionType.Credit, 50m),
            NewTransaction("acct-1", TransactionType.Debit, 75m));
        await _db.SaveChangesAsync();

        var handler = new BalanceQueryHandler(_db);
        var balance = await handler.GetBalanceAsync("acct-1");

        Assert.Equal(275m, balance);
    }

    [Fact]
    public async Task GetBalanceAsync_NoTransactionsForAccount_ReturnsZero()
    {
        var handler = new BalanceQueryHandler(_db);

        var balance = await handler.GetBalanceAsync("acct-nonexistent");

        Assert.Equal(0m, balance);
    }
}
