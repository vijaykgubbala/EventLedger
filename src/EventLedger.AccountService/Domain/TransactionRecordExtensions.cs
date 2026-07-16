namespace EventLedger.AccountService.Domain;

public static class TransactionRecordExtensions
{
    public static decimal ComputeBalance(this IEnumerable<TransactionRecord> transactions) =>
        transactions.Sum(t => t.Type == TransactionType.Credit ? t.Amount : -t.Amount);
}
