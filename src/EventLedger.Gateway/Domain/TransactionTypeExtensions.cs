namespace EventLedger.Gateway.Domain;

public static class TransactionTypeExtensions
{
    public static string ToWireString(this TransactionType type) => type switch
    {
        TransactionType.Credit => "CREDIT",
        TransactionType.Debit => "DEBIT",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static TransactionType ParseWireString(string value) => value switch
    {
        "CREDIT" => TransactionType.Credit,
        "DEBIT" => TransactionType.Debit,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };
}
