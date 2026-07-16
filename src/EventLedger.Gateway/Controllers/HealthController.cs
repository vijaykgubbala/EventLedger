using EventLedger.Gateway.Application;
using Microsoft.AspNetCore.Mvc;

namespace EventLedger.Gateway.Controllers;

[ApiController]
public class HealthController(HealthCheckHandler healthCheckHandler) : ControllerBase
{
    [HttpGet("/health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var canConnect = await healthCheckHandler.CanConnectAsync(cancellationToken);
        return Ok(new { status = canConnect ? "ok" : "degraded", database = canConnect ? "ok" : "unreachable" });
    }
}
