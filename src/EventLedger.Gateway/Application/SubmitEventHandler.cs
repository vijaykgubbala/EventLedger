using System.Net.Http.Json;
using EventLedger.Gateway.Domain;
using EventLedger.Gateway.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.Gateway.Application;

public sealed class SubmitEventHandler(
    GatewayDbContext db,
    IHttpClientFactory httpClientFactory,
    ILogger<SubmitEventHandler> logger)
{
    private const int SqliteConstraintUnique = 2067;

    public async Task<SubmitEventResult> SubmitAsync(
        string eventId,
        string accountId,
        TransactionType type,
        decimal amount,
        string currency,
        DateTimeOffset eventTimestamp,
        string? metadataJson,
        CancellationToken cancellationToken = default)
    {
        var existing = await db.Events.SingleOrDefaultAsync(e => e.EventId == eventId, cancellationToken);
        if (existing is not null)
        {
            WarnIfMismatched(existing, type, amount, currency, eventTimestamp);
            return new SubmitEventResult(SubmitEventOutcome.Duplicate, existing);
        }

        var client = httpClientFactory.CreateClient("AccountService");
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(
                $"/accounts/{accountId}/transactions",
                new { eventId, accountId, type = type.ToWireString(), amount },
                cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Account Service unreachable for eventId {EventId}", eventId);
            return new SubmitEventResult(SubmitEventOutcome.AccountServiceUnavailable, null);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Account Service returned {StatusCode} for eventId {EventId}", response.StatusCode, eventId);
                return new SubmitEventResult(SubmitEventOutcome.AccountServiceUnavailable, null);
            }
        }

        var record = new EventRecord
        {
            EventId = eventId,
            AccountId = accountId,
            Type = type,
            Amount = amount,
            Currency = currency,
            EventTimestamp = eventTimestamp,
            MetadataJson = metadataJson,
            ReceivedAt = DateTimeOffset.UtcNow
        };

        db.Events.Add(record);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new SubmitEventResult(SubmitEventOutcome.Created, record);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteConstraintUnique })
        {
            db.Entry(record).State = EntityState.Detached;

            var winner = await db.Events.SingleAsync(e => e.EventId == eventId, cancellationToken);
            WarnIfMismatched(winner, type, amount, currency, eventTimestamp);
            return new SubmitEventResult(SubmitEventOutcome.Duplicate, winner);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Failed to insert event for eventId {EventId}: constraint violation", eventId);
            return new SubmitEventResult(SubmitEventOutcome.Fault, null);
        }
    }

    private void WarnIfMismatched(EventRecord stored, TransactionType type, decimal amount, string currency, DateTimeOffset eventTimestamp)
    {
        if (stored.Type != type || stored.Amount != amount || stored.Currency != currency || stored.EventTimestamp != eventTimestamp)
        {
            logger.LogWarning(
                "Duplicate eventId {EventId} received with a mismatched payload; returning the originally stored event",
                stored.EventId);
        }
    }
}
