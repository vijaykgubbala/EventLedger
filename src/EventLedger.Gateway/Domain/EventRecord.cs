namespace EventLedger.Gateway.Domain;

public sealed class EventRecord
{
    public long Id { get; set; }
    public string EventId { get; set; } = default!;
    public string AccountId { get; set; } = default!;
    public TransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = default!;
    public DateTimeOffset EventTimestamp { get; set; }
    public string? MetadataJson { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
