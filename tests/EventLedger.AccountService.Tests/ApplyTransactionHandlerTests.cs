using EventLedger.AccountService.Application;
using EventLedger.AccountService.Domain;
using EventLedger.AccountService.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventLedger.AccountService.Tests;

public class ApplyTransactionHandlerTests : IDisposable
{
    private readonly SqliteTempDbFixture _fixture = new();

    public ApplyTransactionHandlerTests()
    {
        using var db = _fixture.CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _fixture.Dispose();

    private AccountDbContext CreateContext() => _fixture.CreateContext();

    private static ApplyTransactionHandler CreateHandler(AccountDbContext db) =>
        new(db, NullLogger<ApplyTransactionHandler>.Instance);

    [Fact]
    public async Task ApplyAsync_NewEventId_InsertsRowAndReturnsCreated()
    {
        using var db = CreateContext();
        var handler = CreateHandler(db);

        var result = await handler.ApplyAsync("evt-1", "acct-1", TransactionType.Credit, 100m);

        Assert.Equal(ApplyTransactionOutcome.Created, result.Outcome);
        Assert.NotNull(result.Transaction);
        Assert.Equal(1, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task ApplyAsync_DuplicateEventId_ReturnsExistingRecordNoSecondRow()
    {
        using (var firstDb = CreateContext())
        {
            await CreateHandler(firstDb).ApplyAsync("evt-2", "acct-1", TransactionType.Credit, 100m);
        }

        using var db = CreateContext();
        var result = await CreateHandler(db).ApplyAsync("evt-2", "acct-1", TransactionType.Credit, 100m);

        Assert.Equal(ApplyTransactionOutcome.Duplicate, result.Outcome);
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.EventId == "evt-2"));
    }

    [Fact]
    public async Task ApplyAsync_ConcurrentDuplicateCalls_ExactlyOneRowBothReturnSameRecord()
    {
        using var db1 = CreateContext();
        using var db2 = CreateContext();

        var task1 = CreateHandler(db1).ApplyAsync("evt-3", "acct-1", TransactionType.Credit, 100m);
        var task2 = CreateHandler(db2).ApplyAsync("evt-3", "acct-1", TransactionType.Credit, 100m);

        var results = await Task.WhenAll(task1, task2);

        using var verifyDb = CreateContext();
        Assert.Equal(1, await verifyDb.Transactions.CountAsync(t => t.EventId == "evt-3"));
        Assert.Equal(results[0].Transaction!.Id, results[1].Transaction!.Id);
    }

    [Fact]
    public async Task ApplyAsync_CheckConstraintViolation_ReturnsFault()
    {
        using var db = CreateContext();
        var handler = CreateHandler(db);

        var result = await handler.ApplyAsync("evt-4", "acct-1", TransactionType.Credit, 0m);

        Assert.Equal(ApplyTransactionOutcome.Fault, result.Outcome);
    }
}
