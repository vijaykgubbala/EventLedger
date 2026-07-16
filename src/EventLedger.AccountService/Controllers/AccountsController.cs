using EventLedger.AccountService.Application;
using EventLedger.AccountService.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EventLedger.AccountService.Controllers;

[ApiController]
[Route("accounts")]
public class AccountsController(
    ApplyTransactionHandler applyHandler,
    BalanceQueryHandler balanceHandler,
    AccountDetailsHandler detailsHandler) : ControllerBase
{
    [HttpPost("{accountId}/transactions")]
    public async Task<IActionResult> ApplyTransaction(
        string accountId, [FromBody] ApplyTransactionRequest request, CancellationToken cancellationToken)
    {
        // Well-formedness only (missing/malformed fields) — not business-rule validation.
        // The Gateway owns amount/type business rules; the DB CHECK constraint is the sole
        // backstop for those, per standards/api.md's anti-pattern against divergent rules.
        if (string.IsNullOrWhiteSpace(request.EventId) ||
            request.Amount is null ||
            request.Type is not ("CREDIT" or "DEBIT"))
        {
            return BadRequest(new
            {
                error = "validation_error",
                message = "eventId, amount, and a valid type (CREDIT or DEBIT) are required."
            });
        }

        var type = TransactionTypeExtensions.ParseWireString(request.Type);

        var result = await applyHandler.ApplyAsync(
            request.EventId, accountId, type, request.Amount.Value, cancellationToken);

        return result.Outcome switch
        {
            ApplyTransactionOutcome.Created => StatusCode(StatusCodes.Status201Created, ToResponse(result.Transaction!)),
            ApplyTransactionOutcome.Duplicate => Ok(ToResponse(result.Transaction!)),
            ApplyTransactionOutcome.Fault => StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "internal_error", message = "The transaction could not be applied." }),
            _ => throw new InvalidOperationException($"Unhandled outcome {result.Outcome}")
        };
    }

    [HttpGet("{accountId}/balance")]
    public async Task<IActionResult> GetBalance(string accountId, CancellationToken cancellationToken)
    {
        var balance = await balanceHandler.GetBalanceAsync(accountId, cancellationToken);
        return Ok(new { accountId, balance });
    }

    [HttpGet("{accountId}")]
    public async Task<IActionResult> GetAccount(string accountId, CancellationToken cancellationToken)
    {
        var details = await detailsHandler.GetDetailsAsync(accountId, cancellationToken);
        return Ok(new
        {
            accountId = details.AccountId,
            balance = details.Balance,
            transactions = details.Transactions.Select(ToResponse)
        });
    }

    private static object ToResponse(TransactionRecord record) => new
    {
        eventId = record.EventId,
        accountId = record.AccountId,
        type = record.Type.ToWireString(),
        amount = record.Amount,
        appliedAt = record.AppliedAt
    };
}
