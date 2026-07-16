using EventLedger.AccountService.Infrastructure;

namespace EventLedger.AccountService.Application;

public sealed class HealthCheckHandler(AccountDbContext db)
{
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) =>
        db.Database.CanConnectAsync(cancellationToken);
}
