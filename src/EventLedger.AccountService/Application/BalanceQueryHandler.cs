using EventLedger.AccountService.Domain;
using EventLedger.AccountService.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.AccountService.Application;

public sealed class BalanceQueryHandler(AccountDbContext db)
{
    public async Task<decimal> GetBalanceAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var amounts = await db.Transactions
            .Where(t => t.AccountId == accountId)
            .Select(t => new { t.Type, t.Amount })
            .ToListAsync(cancellationToken);

        return amounts.Sum(t => t.Type == TransactionType.Credit ? t.Amount : -t.Amount);
    }
}
