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

    // An unescaped accountId containing '?' would otherwise let its suffix be parsed as the
    // request's query string, truncating the path before "/balance" and silently redirecting the
    // outbound call to a different Account Service route than intended.
    [Fact]
    public async Task GetBalanceAsync_AccountIdContainsReservedCharacters_EscapesBeforeSendingRequest()
    {
        var captor = new CapturingHandler();
        var handler = new BalanceQueryHandler(new StubHttpClientFactory(captor));

        await handler.GetBalanceAsync("acct-1?evil=1");

        Assert.NotNull(captor.CapturedUri);
        Assert.Equal("/accounts/acct-1%3Fevil%3D1/balance", captor.CapturedUri!.AbsolutePath);
        Assert.Equal(string.Empty, captor.CapturedUri.Query);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? CapturedUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler) { BaseAddress = new Uri("http://localhost:5199") };

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated: Account Service unreachable");
    }
}
