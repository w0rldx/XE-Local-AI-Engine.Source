namespace XE_Local_AI_Engine.Client.Testing.Fakes;

using System.Net;

/// <summary>An <see cref="HttpMessageHandler" /> that answers every send 503 and counts the attempts, for retry tests.</summary>
public sealed class UnavailableHttpMessageHandler : HttpMessageHandler
{
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Interlocked.Increment(ref _attempts);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
