using EventLedger.Gateway.Domain;

namespace EventLedger.Gateway.Application;

public enum SubmitEventOutcome
{
    Created,
    Duplicate,
    AccountServiceUnavailable,
    Fault
}

public sealed record SubmitEventResult(SubmitEventOutcome Outcome, EventRecord? Event);
