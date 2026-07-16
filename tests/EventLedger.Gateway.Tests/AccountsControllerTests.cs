using System.Net;
using System.Net.Http.Json;
using EventLedger.Gateway.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventLedger.Gateway.Tests;

public class AccountsControllerTests : IDisposable
{
    private readonly SqliteTempDbFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private WebApplicationFactory<Program> CreateFactory(HttpMessageHandler accountServiceHandler) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<GatewayDbContext>>();
                services.AddDbContext<GatewayDbContext>(opt => opt.UseSqlite(_fixture.ConnectionString));

                services.AddHttpClient("AccountService")
                    .ConfigurePrimaryHttpMessageHandler(() => accountServiceHandler);
            });
        });

    [Fact]
    public async Task GetBalance_AccountServiceReachable_ReturnsBalanceBodyVerbatim()
    {
        const string body = """{"accountId":"acct-1","balance":150}""";
        using var factory = CreateFactory(new StubBalanceHandler(HttpStatusCode.OK, body));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/accounts/acct-1/balance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetBalance_AccountServiceUnreachable_Returns503WithStandardEnvelope()
    {
        using var factory = CreateFactory(new StubBalanceHandler(HttpStatusCode.InternalServerError, string.Empty));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/accounts/acct-1/balance");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        Assert.Equal("account_service_unavailable", error!.Error);
        Assert.Equal("The Account Service is currently unavailable.", error.Message);
    }

    private sealed class StubBalanceHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed record ErrorResponseDto(string Error, string Message);
}
