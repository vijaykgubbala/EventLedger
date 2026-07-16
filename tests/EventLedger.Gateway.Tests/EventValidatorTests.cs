using System.Text.Json;
using EventLedger.Gateway.Application;

namespace EventLedger.Gateway.Tests;

public class EventValidatorTests
{
    private readonly EventValidator _validator = new();

    private static (string? eventId, string? accountId, string? type, decimal? amount, string? currency, string? eventTimestamp) ValidPayload() =>
        ("evt-1", "acct-1", "CREDIT", 100m, "USD", "2026-05-15T14:02:11Z");

    [Theory]
    [InlineData("eventId")]
    [InlineData("accountId")]
    [InlineData("type")]
    [InlineData("amount")]
    [InlineData("currency")]
    [InlineData("eventTimestamp")]
    public void Validate_EachRequiredFieldMissingIndividually_FailureNamesThatField(string missingField)
    {
        var (eventId, accountId, type, amount, currency, eventTimestamp) = ValidPayload();

        var failures = _validator.Validate(
            eventId: missingField == "eventId" ? null : eventId,
            accountId: missingField == "accountId" ? null : accountId,
            type: missingField == "type" ? null : type,
            amount: missingField == "amount" ? null : amount,
            currency: missingField == "currency" ? null : currency,
            eventTimestamp: missingField == "eventTimestamp" ? null : eventTimestamp);

        Assert.Contains(failures, f => f.Field == missingField);
    }

    [Fact]
    public void Validate_AmountNotGreaterThanZero_Fails()
    {
        var (eventId, accountId, type, _, currency, eventTimestamp) = ValidPayload();

        var failures = _validator.Validate(eventId, accountId, type, 0m, currency, eventTimestamp);

        Assert.Contains(failures, f => f.Field == "amount");
    }

    [Fact]
    public void Validate_MalformedEventTimestamp_Fails()
    {
        var (eventId, accountId, type, amount, currency, _) = ValidPayload();

        var failures = _validator.Validate(eventId, accountId, type, amount, currency, "not-a-date");

        Assert.Contains(failures, f => f.Field == "eventTimestamp");
    }

    [Theory]
    [InlineData("credit")]
    [InlineData("Credit")]
    [InlineData("PAYMENT")]
    public void Validate_TypeNotExactlyCreditOrDebit_Fails(string invalidType)
    {
        var (eventId, accountId, _, amount, currency, eventTimestamp) = ValidPayload();

        var failures = _validator.Validate(eventId, accountId, invalidType, amount, currency, eventTimestamp);

        Assert.Contains(failures, f => f.Field == "type");
    }

    [Fact]
    public void Validate_FullyValidPayload_NoFailures()
    {
        var (eventId, accountId, type, amount, currency, eventTimestamp) = ValidPayload();

        var failures = _validator.Validate(eventId, accountId, type, amount, currency, eventTimestamp);

        Assert.Empty(failures);
    }

    [Fact]
    public void Validate_MetadataPresentButNotAnObject_Fails()
    {
        var (eventId, accountId, type, amount, currency, eventTimestamp) = ValidPayload();
        var metadata = JsonSerializer.SerializeToElement("not an object");

        var failures = _validator.Validate(eventId, accountId, type, amount, currency, eventTimestamp, metadata);

        Assert.Contains(failures, f => f.Field == "metadata");
    }

    [Fact]
    public void Validate_MetadataIsAnObject_NoMetadataFailure()
    {
        var (eventId, accountId, type, amount, currency, eventTimestamp) = ValidPayload();
        var metadata = JsonSerializer.SerializeToElement(new { source = "test" });

        var failures = _validator.Validate(eventId, accountId, type, amount, currency, eventTimestamp, metadata);

        Assert.DoesNotContain(failures, f => f.Field == "metadata");
    }

    [Fact]
    public void Validate_MetadataAbsent_NoMetadataFailure()
    {
        var (eventId, accountId, type, amount, currency, eventTimestamp) = ValidPayload();

        var failures = _validator.Validate(eventId, accountId, type, amount, currency, eventTimestamp, metadata: null);

        Assert.DoesNotContain(failures, f => f.Field == "metadata");
    }
}
