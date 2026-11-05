namespace ApiAggregation.IntegrationTests.Support;

internal sealed class ProviderHttpStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public ProviderHttpStub(Func<HttpRequestMessage, HttpResponseMessage> send)
        : this((request, _) => Task.FromResult(send(request)))
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return send(request, cancellationToken);
    }
}
