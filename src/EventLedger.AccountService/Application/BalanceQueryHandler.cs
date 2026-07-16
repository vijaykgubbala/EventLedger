using EventLedger.AccountService.Domain;
using EventLedger.AccountService.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.AccountService.Application;

public sealed class BalanceQueryHandler(AccountDbContext db)
{
    public async Task<decimal> GetBalanceAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var transactions = await db.Transactions
            .AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .ToListAsync(cancellationToken);

        return transactions.ComputeBalance();
    }
}
