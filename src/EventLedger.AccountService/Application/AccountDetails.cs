using EventLedger.AccountService.Domain;

namespace EventLedger.AccountService.Application;

public sealed record AccountDetails(string AccountId, decimal Balance, IReadOnlyList<TransactionRecord> Transactions);
