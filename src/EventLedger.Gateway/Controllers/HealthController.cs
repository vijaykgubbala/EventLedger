using Microsoft.AspNetCore.Mvc;

namespace EventLedger.Gateway.Controllers;

[ApiController]
public class HealthController : ControllerBase
{
    [HttpGet("/health")]
    public IActionResult Health() => Ok(new { status = "ok" });
}
