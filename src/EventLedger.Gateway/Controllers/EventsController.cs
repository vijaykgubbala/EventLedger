using System.Globalization;
using System.Text.Json;
using EventLedger.Gateway.Application;
using EventLedger.Gateway.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EventLedger.Gateway.Controllers;

[ApiController]
[Route("events")]
public class EventsController(
    EventValidator validator,
    SubmitEventHandler submitHandler,
    EventQueryHandler queryHandler) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] SubmitEventRequest request, CancellationToken cancellationToken)
    {
        var failures = validator.Validate(
            request.EventId, request.AccountId, request.Type, request.Amount, request.Currency, request.EventTimestamp);

        if (failures.Count > 0)
        {
            var first = failures[0];
            return BadRequest(new { error = "validation_error", message = first.Message, details = new { field = first.Field } });
        }

        var type = TransactionTypeExtensions.ParseWireString(request.Type!);
        var eventTimestamp = DateTimeOffset.Parse(request.EventTimestamp!, CultureInfo.InvariantCulture, DateTimeStyles.None);
        var metadataJson = request.Metadata?.GetRawText();

        var result = await submitHandler.SubmitAsync(
            request.EventId!, request.AccountId!, type, request.Amount!.Value, request.Currency!, eventTimestamp, metadataJson, cancellationToken);

        return result.Outcome switch
        {
            SubmitEventOutcome.Created => StatusCode(StatusCodes.Status201Created, ToResponse(result.Event!)),
            SubmitEventOutcome.Duplicate => Ok(ToResponse(result.Event!)),
            SubmitEventOutcome.AccountServiceUnavailable => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "account_service_unavailable", message = "The Account Service is currently unavailable." }),
            SubmitEventOutcome.Fault => StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "internal_error", message = "The event could not be recorded." }),
            _ => throw new InvalidOperationException($"Unhandled outcome {result.Outcome}")
        };
    }

    [HttpGet("{eventId}")]
    public async Task<IActionResult> GetById(string eventId, CancellationToken cancellationToken)
    {
        var record = await queryHandler.GetByIdAsync(eventId, cancellationToken);

        return record is null
            ? NotFound(new { error = "not_found", message = $"No event found with eventId '{eventId}'" })
            : Ok(ToResponse(record));
    }

    [HttpGet]
    public async Task<IActionResult> ListByAccount([FromQuery] string account, CancellationToken cancellationToken)
    {
        var records = await queryHandler.ListByAccountAsync(account, cancellationToken);
        return Ok(records.Select(ToResponse));
    }

    private static object ToResponse(EventRecord record) => new
    {
        eventId = record.EventId,
        accountId = record.AccountId,
        type = record.Type.ToWireString(),
        amount = record.Amount,
        currency = record.Currency,
        eventTimestamp = record.EventTimestamp,
        metadata = record.MetadataJson is null ? (object?)null : JsonSerializer.Deserialize<JsonElement>(record.MetadataJson),
        receivedAt = record.ReceivedAt
    };
}
