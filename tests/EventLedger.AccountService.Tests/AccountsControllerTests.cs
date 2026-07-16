using System.Net;
using System.Net.Http.Json;
using EventLedger.AccountService.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventLedger.AccountService.Tests;

public class AccountsControllerTests : IDisposable
{
    private readonly SqliteTempDbFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AccountDbContext>>();
                services.AddDbContext<AccountDbContext>(opt => opt.UseSqlite(_fixture.ConnectionString));
            });
        });

    private static object ValidPayload(string eventId, string accountId = "acct-1") => new
    {
        eventId,
        accountId,
        type = "CREDIT",
        amount = 100m
    };

    [Fact]
    public async Task PostTransactions_ValidPayload_Returns201WithRecord()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/accounts/acct-1/transactions", ValidPayload("evt-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TransactionResponseDto>();
        Assert.Equal("evt-1", body!.EventId);
        Assert.Equal("acct-1", body.AccountId);
    }

    [Fact]
    public async Task PostTransactions_DuplicateEventId_Returns200WithOriginal()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/accounts/acct-1/transactions", ValidPayload("evt-2"));

        var response = await client.PostAsJsonAsync("/accounts/acct-1/transactions", ValidPayload("evt-2"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TransactionResponseDto>();
        Assert.Equal("evt-2", body!.EventId);
    }

    [Fact]
    public async Task GetBalance_ReturnsComputedBalance()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/accounts/acct-2/transactions", new { eventId = "evt-3", accountId = "acct-2", type = "CREDIT", amount = 300m });
        await client.PostAsJsonAsync("/accounts/acct-2/transactions", new { eventId = "evt-4", accountId = "acct-2", type = "DEBIT", amount = 50m });

        var response = await client.GetAsync("/accounts/acct-2/balance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BalanceResponseDto>();
        Assert.Equal(250m, body!.Balance);
    }

    [Fact]
    public async Task PostTransactions_MissingEventId_Returns400WithErrorShape()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/accounts/acct-1/transactions", new { accountId = "acct-1", type = "CREDIT", amount = 100m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        Assert.Equal("validation_error", body!.Error);
    }

    [Fact]
    public async Task PostTransactions_MissingAmount_Returns400WithErrorShape()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/accounts/acct-1/transactions", new { eventId = "evt-missing-amount", accountId = "acct-1", type = "CREDIT" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostTransactions_InvalidType_Returns400WithErrorShape()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/accounts/acct-1/transactions", new { eventId = "evt-invalid-type", accountId = "acct-1", type = "PAYMENT", amount = 100m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAccount_ReturnsAccountIdAndTransactionList()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/accounts/acct-3/transactions", new { eventId = "evt-5", accountId = "acct-3", type = "CREDIT", amount = 200m });

        var response = await client.GetAsync("/accounts/acct-3");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AccountDetailsResponseDto>();
        Assert.Equal("acct-3", body!.AccountId);
        Assert.Single(body.Transactions);
    }

    private sealed record TransactionResponseDto(string EventId, string AccountId, string Type, decimal Amount, DateTimeOffset AppliedAt);

    private sealed record BalanceResponseDto(string AccountId, decimal Balance);

    private sealed record AccountDetailsResponseDto(string AccountId, decimal Balance, List<TransactionResponseDto> Transactions);

    private sealed record ErrorResponseDto(string Error, string Message, object? Details);
}
