using System.Text.Json;

namespace EventLedger.Gateway.Controllers;

public sealed record SubmitEventRequest(
    string? EventId,
    string? AccountId,
    string? Type,
    decimal? Amount,
    string? Currency,
    string? EventTimestamp,
    JsonElement? Metadata);
