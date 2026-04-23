namespace XE_Local_AI_Engine.Tests.Testing.Mocks;

using System.Net;
using System.Net.Http.Json;

public sealed class MockOllamaHttpHandler : HttpMessageHandler
{
    private readonly object _sync = new();
    private string[] _models = [];
    private Exception? _nextException;

    public int TagsRequestCount { get; private set; }

    public void SetModelsResponse(params string[] models)
    {
        ArgumentNullException.ThrowIfNull(models);

        lock (_sync)
        {
            _models = models.ToArray();
        }
    }

    public void ThrowOnNextRequest(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_sync)
        {
            _nextException = exception;
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ThrowIfScheduled();

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (string.Equals(path, "/api/tags", StringComparison.OrdinalIgnoreCase))
        {
            lock (_sync)
            {
                TagsRequestCount++;
            }

            var models = GetModelsSnapshot()
                         .Select(name => new MockOllamaModel(name))
                         .ToArray();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new MockOllamaTagsResponse(models))
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
            })
        });
    }

    private string[] GetModelsSnapshot()
    {
        lock (_sync)
        {
            return _models.ToArray();
        }
    }

    private void ThrowIfScheduled()
    {
        Exception? exceptionToThrow;

        lock (_sync)
        {
            exceptionToThrow = _nextException;
            _nextException = null;
        }

        if (exceptionToThrow is not null)
        {
            throw exceptionToThrow;
        }
    }

    private sealed record MockOllamaTagsResponse(IReadOnlyList<MockOllamaModel> Models);

    private sealed record MockOllamaModel(string Name);
}
