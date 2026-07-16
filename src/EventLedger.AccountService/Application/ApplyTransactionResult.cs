using EventLedger.AccountService.Domain;

namespace EventLedger.AccountService.Application;

public enum ApplyTransactionOutcome
{
    Created,
    Duplicate,
    Fault
}

public sealed record ApplyTransactionResult(ApplyTransactionOutcome Outcome, TransactionRecord? Transaction);
