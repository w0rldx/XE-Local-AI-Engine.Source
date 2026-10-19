namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The <c>/v1/systemone</c> seam, scripted per question: each test names its own question and the result the
///     model "gave", and reads back what the provider sent.
/// </summary>
internal sealed class FakeSystemOneClient : ILlamaServerSystemOneClient
{
    private readonly ConcurrentDictionary<string, LlamaServerSystemOneResult> _scripts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LlamaServerSystemOneRequest> _requests = new(StringComparer.Ordinal);

    public void Script(string question, LlamaServerSystemOneResult result) =>
        _scripts[question] = result;

    /// <summary>What the provider sent for <paramref name="question" />, or null when it never asked.</summary>
    public LlamaServerSystemOneRequest? RequestFor(string question) =>
        _requests.TryGetValue(question, out var request) ? request : null;

    public Task<LlamaServerSystemOneResult> DecideAsync(LlamaServerSystemOneRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requests[request.Instructions] = request;
        return Task.FromResult(_scripts.TryGetValue(request.Instructions, out var scripted)
            ? scripted
            : new LlamaServerSystemOneResult
            {
                Outcome = LlamaServerSystemOneOutcome.Transport,
                Detail = "No answer was scripted for this question."
            });
    }
}
