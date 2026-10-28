namespace XE_Local_AI_Engine.Client.Testing.Fakes;

/// <summary>An <see cref="HttpMessageHandler" /> that answers every send from a responder and counts the sends.</summary>
public sealed class CountingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpResponseMessage> _responder;

    public CountingHttpMessageHandler(Func<HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(_responder());
    }
}
