namespace EventLedger.AccountService.Controllers;

public sealed record ApplyTransactionRequest(string? EventId, string? AccountId, string? Type, decimal? Amount);
