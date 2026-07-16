using System.Globalization;
using System.Text.Json;

namespace EventLedger.Gateway.Application;

public sealed class EventValidator
{
    public IReadOnlyList<ValidationFailure> Validate(
        string? eventId,
        string? accountId,
        string? type,
        decimal? amount,
        string? currency,
        string? eventTimestamp,
        JsonElement? metadata = null)
    {
        var failures = new List<ValidationFailure>();

        if (string.IsNullOrWhiteSpace(eventId))
        {
            failures.Add(new ValidationFailure("eventId", "eventId is required"));
        }

        if (string.IsNullOrWhiteSpace(accountId))
        {
            failures.Add(new ValidationFailure("accountId", "accountId is required"));
        }

        if (type is not ("CREDIT" or "DEBIT"))
        {
            failures.Add(new ValidationFailure("type", "type must be exactly \"CREDIT\" or \"DEBIT\""));
        }

        if (amount is null)
        {
            failures.Add(new ValidationFailure("amount", "amount is required"));
        }
        else if (amount <= 0)
        {
            failures.Add(new ValidationFailure("amount", "amount must be greater than 0"));
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            failures.Add(new ValidationFailure("currency", "currency is required"));
        }

        if (string.IsNullOrWhiteSpace(eventTimestamp) ||
            !DateTimeOffset.TryParse(eventTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            failures.Add(new ValidationFailure("eventTimestamp", "eventTimestamp must be a valid ISO 8601 timestamp"));
        }

        if (metadata is { ValueKind: not JsonValueKind.Object })
        {
            failures.Add(new ValidationFailure("metadata", "metadata must be a JSON object"));
        }

        return failures;
    }
}
