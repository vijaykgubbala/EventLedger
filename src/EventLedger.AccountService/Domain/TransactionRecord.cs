namespace EventLedger.AccountService.Domain;

public sealed class TransactionRecord
{
    public long Id { get; set; }
    public string EventId { get; set; } = default!;
    public string AccountId { get; set; } = default!;
    public TransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
}
