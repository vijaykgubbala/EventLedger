using Polly;

namespace EventLedger.Gateway.Application;

public sealed class BalanceQueryHandler(IHttpClientFactory httpClientFactory)
{
    public async Task<BalanceQueryResult> GetBalanceAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("AccountService");
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync($"/accounts/{accountId}/balance", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException)
        {
            return new BalanceQueryResult(BalanceQueryOutcome.AccountServiceUnavailable, null);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return new BalanceQueryResult(BalanceQueryOutcome.AccountServiceUnavailable, null);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new BalanceQueryResult(BalanceQueryOutcome.Success, body);
        }
    }
}

public enum BalanceQueryOutcome
{
    Success,
    AccountServiceUnavailable
}

public sealed record BalanceQueryResult(BalanceQueryOutcome Outcome, string? Body);
