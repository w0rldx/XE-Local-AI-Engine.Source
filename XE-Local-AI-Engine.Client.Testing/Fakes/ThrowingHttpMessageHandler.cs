namespace XE_Local_AI_Engine.Client.Testing.Fakes;

/// <summary>An <see cref="HttpMessageHandler" /> whose every send fails with the exception it was built with.</summary>
public sealed class ThrowingHttpMessageHandler : HttpMessageHandler
{
    private readonly Exception _exception;

    public ThrowingHttpMessageHandler(Exception exception)
    {
        _exception = exception;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromException<HttpResponseMessage>(_exception);
    }
}
