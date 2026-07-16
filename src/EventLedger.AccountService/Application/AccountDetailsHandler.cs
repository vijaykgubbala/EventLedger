using EventLedger.AccountService.Domain;
using EventLedger.AccountService.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.AccountService.Application;

public sealed class AccountDetailsHandler(AccountDbContext db)
{
    public async Task<AccountDetails> GetDetailsAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var transactions = await db.Transactions
            .Where(t => t.AccountId == accountId)
            .ToListAsync(cancellationToken);

        var ordered = transactions.OrderBy(t => t.AppliedAt).ToList();
        var balance = ordered.Sum(t => t.Type == TransactionType.Credit ? t.Amount : -t.Amount);

        return new AccountDetails(accountId, balance, ordered);
    }
}
