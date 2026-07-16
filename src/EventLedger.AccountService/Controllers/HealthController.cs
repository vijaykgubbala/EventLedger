using EventLedger.AccountService.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace EventLedger.AccountService.Controllers;

[ApiController]
public class HealthController(AccountDbContext db) : ControllerBase
{
    [HttpGet("/health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var canConnect = await db.Database.CanConnectAsync(cancellationToken);
        return Ok(new { status = canConnect ? "ok" : "degraded", database = canConnect ? "ok" : "unreachable" });
    }
}
