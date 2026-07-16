namespace EventLedger.Gateway.Application;

public sealed record ValidationFailure(string Field, string Message);
