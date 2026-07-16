using EventLedger.Gateway.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace EventLedger.Gateway.Controllers;

[ApiController]
public class HealthController(GatewayDbContext db) : ControllerBase
{
    [HttpGet("/health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var canConnect = await db.Database.CanConnectAsync(cancellationToken);
        return Ok(new { status = canConnect ? "ok" : "degraded", database = canConnect ? "ok" : "unreachable" });
    }
}
