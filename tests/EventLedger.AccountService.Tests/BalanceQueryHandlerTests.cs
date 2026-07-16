using EventLedger.AccountService.Application;
using EventLedger.AccountService.Domain;
using EventLedger.AccountService.Infrastructure;

namespace EventLedger.AccountService.Tests;

public class BalanceQueryHandlerTests : IDisposable
{
    private readonly SqliteTempDbFixture _fixture = new();
    private readonly AccountDbContext _db;

    public BalanceQueryHandlerTests()
    {
        _db = _fixture.CreateContext();
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _fixture.Dispose();
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

    [Fact]
    public async Task GetBalanceAsync_AllCredits_ReturnsSumOfCredits()
    {
        _db.Transactions.AddRange(
            NewTransaction("acct-3", TransactionType.Credit, 100m),
            NewTransaction("acct-3", TransactionType.Credit, 50m));
        await _db.SaveChangesAsync();

        var handler = new BalanceQueryHandler(_db);
        var balance = await handler.GetBalanceAsync("acct-3");

        Assert.Equal(150m, balance);
    }

    [Fact]
    public async Task GetBalanceAsync_AllDebits_ReturnsNegativeSumOfDebits()
    {
        _db.Transactions.AddRange(
            NewTransaction("acct-4", TransactionType.Debit, 40m),
            NewTransaction("acct-4", TransactionType.Debit, 60m));
        await _db.SaveChangesAsync();

        var handler = new BalanceQueryHandler(_db);
        var balance = await handler.GetBalanceAsync("acct-4");

        Assert.Equal(-100m, balance);
    }
}
