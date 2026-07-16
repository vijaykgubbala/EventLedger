using Microsoft.AspNetCore.Mvc.Testing;

namespace EventLedger.AccountService.Tests;

public class AccountServiceBootTests
{
    [Fact]
    public async Task Application_StartsAndAcceptsRequests()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.True(response.IsSuccessStatusCode, $"Expected a success status code, got {response.StatusCode}");
    }
}
