using Microsoft.AspNetCore.Mvc;

namespace EventLedger.AccountService.Controllers;

[ApiController]
public class HealthController : ControllerBase
{
    [HttpGet("/health")]
    public IActionResult Health() => Ok(new { status = "ok" });
}
