using EventLedger.AccountService.Domain;
using EventLedger.AccountService.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.AccountService.Application;

public sealed class ApplyTransactionHandler(AccountDbContext db, ILogger<ApplyTransactionHandler> logger)
{
    private const int SqliteConstraintUnique = 2067;

    public async Task<ApplyTransactionResult> ApplyAsync(
        string eventId,
        string accountId,
        TransactionType type,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        var record = new TransactionRecord
        {
            EventId = eventId,
            AccountId = accountId,
            Type = type,
            Amount = amount,
            AppliedAt = DateTimeOffset.UtcNow
        };

        db.Transactions.Add(record);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new ApplyTransactionResult(ApplyTransactionOutcome.Created, record);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteConstraintUnique })
        {
            db.Entry(record).State = EntityState.Detached;

            var existing = await db.Transactions.SingleAsync(t => t.EventId == eventId, cancellationToken);
            if (existing.Type != type || existing.Amount != amount)
            {
                logger.LogWarning(
                    "Duplicate eventId {EventId} received with a mismatched payload; returning the originally stored transaction",
                    eventId);
            }

            return new ApplyTransactionResult(ApplyTransactionOutcome.Duplicate, existing);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Failed to apply transaction for eventId {EventId}: constraint violation", eventId);
            return new ApplyTransactionResult(ApplyTransactionOutcome.Fault, null);
        }
    }
}
