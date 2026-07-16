using EventLedger.Gateway.Domain;
using EventLedger.Gateway.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.Gateway.Application;

public sealed class EventQueryHandler(GatewayDbContext db)
{
    public Task<EventRecord?> GetByIdAsync(string eventId, CancellationToken cancellationToken = default) =>
        db.Events.SingleOrDefaultAsync(e => e.EventId == eventId, cancellationToken);

    public async Task<IReadOnlyList<EventRecord>> ListByAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var events = await db.Events
            .Where(e => e.AccountId == accountId)
            .ToListAsync(cancellationToken);

        return events.OrderBy(e => e.EventTimestamp).ToList();
    }
}
