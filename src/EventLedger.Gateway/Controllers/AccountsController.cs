using EventLedger.Gateway.Application;
using Microsoft.AspNetCore.Mvc;

namespace EventLedger.Gateway.Controllers;

[ApiController]
[Route("accounts")]
public class AccountsController(BalanceQueryHandler balanceQueryHandler) : ControllerBase
{
    [HttpGet("{accountId}/balance")]
    public async Task<IActionResult> GetBalance(string accountId, CancellationToken cancellationToken)
    {
        var result = await balanceQueryHandler.GetBalanceAsync(accountId, cancellationToken);

        return result.Outcome switch
        {
            BalanceQueryOutcome.Success => Content(result.Body!, "application/json"),
            BalanceQueryOutcome.AccountServiceUnavailable => this.AccountServiceUnavailable(),
            _ => throw new InvalidOperationException($"Unhandled outcome {result.Outcome}")
        };
    }
}
