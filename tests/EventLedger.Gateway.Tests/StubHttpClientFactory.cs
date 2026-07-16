namespace EventLedger.Gateway.Tests;

internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    private readonly HttpClient _client = new(handler) { BaseAddress = new Uri("http://localhost:5199") };

    public HttpClient CreateClient(string name) => _client;
}
