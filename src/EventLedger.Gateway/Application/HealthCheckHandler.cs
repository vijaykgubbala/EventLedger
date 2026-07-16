using EventLedger.Gateway.Infrastructure;

namespace EventLedger.Gateway.Application;

public sealed class HealthCheckHandler(GatewayDbContext db)
{
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) =>
        db.Database.CanConnectAsync(cancellationToken);
}
