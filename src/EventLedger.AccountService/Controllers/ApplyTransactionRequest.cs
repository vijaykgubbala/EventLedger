namespace EventLedger.AccountService.Controllers;

public sealed record ApplyTransactionRequest(string? EventId, string? Type, decimal? Amount);
