using Microsoft.AspNetCore.Mvc;

namespace EventLedger.Gateway.Controllers;

internal static class ControllerBaseExtensions
{
    public static IActionResult AccountServiceUnavailable(this ControllerBase controller) =>
        controller.StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new { error = "account_service_unavailable", message = "The Account Service is currently unavailable." });
}
