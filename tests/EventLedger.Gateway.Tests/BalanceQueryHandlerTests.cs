using System.Net;
using EventLedger.Gateway.Application;

namespace EventLedger.Gateway.Tests;

public class BalanceQueryHandlerTests
{
    [Fact]
    public async Task GetBalanceAsync_AccountServiceReturnsSuccess_ReturnsBodyVerbatim()
    {
        const string body = """{"accountId":"acct-1","balance":150}""";
        var handler = new BalanceQueryHandler(new StubHttpClientFactory(new StubResponseHandler(HttpStatusCode.OK, body)));

        var result = await handler.GetBalanceAsync("acct-1");

        Assert.Equal(BalanceQueryOutcome.Success, result.Outcome);
        Assert.Equal(body, result.Body);
    }

    [Fact]
    public async Task GetBalanceAsync_AccountServiceUnreachable_ReturnsUnavailable()
    {
        var handler = new BalanceQueryHandler(new StubHttpClientFactory(new ThrowingHandler()));

        var result = await handler.GetBalanceAsync("acct-1");

        Assert.Equal(BalanceQueryOutcome.AccountServiceUnavailable, result.Outcome);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task GetBalanceAsync_AccountServiceReturnsNonSuccessStatus_ReturnsUnavailable()
    {
        var handler = new BalanceQueryHandler(new StubHttpClientFactory(new StubResponseHandler(HttpStatusCode.InternalServerError, string.Empty)));

        var result = await handler.GetBalanceAsync("acct-1");

        Assert.Equal(BalanceQueryOutcome.AccountServiceUnavailable, result.Outcome);
        Assert.Null(result.Body);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler) { BaseAddress = new Uri("http://localhost:5199") };

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StubResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated: Account Service unreachable");
    }
}
