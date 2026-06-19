namespace XE_Local_AI_Engine.Tests.PreviewWorkflows;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     A scripted <see cref="IPreviewWorkflowRunSession" /> that yields a queued sequence of
///     <see cref="PreviewWorkflowUpdate" />s. No Ollama / network — the entire run is driven from a channel the test
///     fills. <see cref="ResumeAsync" /> lets the test enqueue the post-pause continuation.
/// </summary>
internal sealed class ScriptedPreviewRunSession : IPreviewWorkflowRunSession
{
    private readonly Func<string, ScriptedPreviewRunSession, Task>? _onResume;

    private readonly Channel<PreviewWorkflowUpdate> _updates =
        Channel.CreateUnbounded<PreviewWorkflowUpdate>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

    public ScriptedPreviewRunSession(IEnumerable<PreviewWorkflowUpdate> initialUpdates,
        Func<string, ScriptedPreviewRunSession, Task>? onResume = null)
    {
        ArgumentNullException.ThrowIfNull(initialUpdates);
        _onResume = onResume;
        foreach (var update in initialUpdates)
        {
            _ = _updates.Writer.TryWrite(update);
        }
    }

    public bool Disposed { get; private set; }

    public int ResumeCount { get; private set; }

    public async IAsyncEnumerable<PreviewWorkflowUpdate> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _updates.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_updates.Reader.TryRead(out var update))
            {
                yield return update;

                if (update.Kind == PreviewWorkflowUpdateKind.RunPaused
                    || update.Kind == PreviewWorkflowUpdateKind.RunCompleted
                    || update.Kind == PreviewWorkflowUpdateKind.RunFailed
                    || update.Kind == PreviewWorkflowUpdateKind.NodeFailed)
                {
                    // Terminal/pause: end this enumeration (mirrors the real session's break-on-pause behavior).
                    yield break;
                }
            }
        }
    }

    public async Task ResumeAsync(string requestId, CancellationToken cancellationToken = default)
    {
        ResumeCount++;
        if (_onResume is not null)
        {
            await _onResume(requestId, this).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _ = _updates.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>Enqueues another update for the next (or in-flight) WatchAsync enumeration.</summary>
    public void Enqueue(PreviewWorkflowUpdate update)
    {
        _ = _updates.Writer.TryWrite(update);
    }

    /// <summary>Signals the current WatchAsync enumeration to end (without a terminal update) — e.g. to pause.</summary>
    public void CompleteStream()
    {
        _ = _updates.Writer.TryComplete();
    }
}

/// <summary>
///     A <see cref="IPreviewWorkflowRunner" /> whose <see cref="StartAsync" /> returns a caller-supplied scripted
///     session. The factory receives the definition and the client the resolver returns for the FIRST agent node's
///     model (so existing tests can still assert the node-local client is the one handed in); the raw resolver is also
///     captured for tests that exercise per-model resolution.
/// </summary>
internal sealed class FakePreviewWorkflowRunner(Func<PreviewWorkflowDefinition, IChatClient, ScriptedPreviewRunSession> factory)
    : IPreviewWorkflowRunner
{
    private readonly Func<PreviewWorkflowDefinition, IChatClient, ScriptedPreviewRunSession> _factory =
        factory ?? throw new ArgumentNullException(nameof(factory));

    public IChatClient? LastChatClient { get; private set; }

    public Func<string, IChatClient>? LastResolver { get; private set; }

    public Task<IPreviewWorkflowRunSession> StartAsync(PreviewWorkflowDefinition definition,
        Func<string, IChatClient> resolveChatClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolveChatClient);
        LastResolver = resolveChatClient;

        // Resolve the first agent node's client so existing assertions over the handed-in node-local client hold.
        var firstAgentModelId = definition.Nodes
                                          .OfType<PreviewAgentNode>()
                                          .Select(static node => node.ModelId)
                                          .FirstOrDefault();
        var chatClient = firstAgentModelId is not null ? resolveChatClient(firstAgentModelId) : null!;
        LastChatClient = chatClient;
        return Task.FromResult<IPreviewWorkflowRunSession>(_factory(definition, chatClient));
    }
}

/// <summary>A no-op <see cref="IChatClient" /> stub. Tracks disposal so handle teardown can be asserted.</summary>
internal sealed class FakeNodeLocalChatClient : IChatClient
{
    public bool Disposed { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return EmptyStream();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    public void Dispose()
    {
        Disposed = true;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}

/// <summary>
///     A node-local <see cref="ILocalModelProvider" /> stub that hands out a tracked <see cref="FakeNodeLocalChatClient" />.
///     Only the chat-client surface is exercised; the rest throws (proving they are never called from the run path).
/// </summary>
internal sealed class FakeLocalModelProvider : ILocalModelProvider
{
    public const string FakeProviderName = "fake-node-local";

    public List<FakeNodeLocalChatClient> CreatedClients { get; } = [];

    public string ProviderName => FakeProviderName;

    public IChatClient CreateChatClient(LocalModelSelection selection)
    {
        var client = new FakeNodeLocalChatClient();
        CreatedClients.Add(client);
        return client;
    }

    public Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct)
    {
        throw new NotSupportedException();
    }

    public Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct)
    {
        throw new NotSupportedException();
    }

    public Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        throw new NotSupportedException();
    }

    public Task DeleteModelAsync(string modelName, CancellationToken ct)
    {
        throw new NotSupportedException();
    }

    public Task WarmModelAsync(string modelName, CancellationToken ct)
    {
        throw new NotSupportedException();
    }

    public Task UnloadModelAsync(string modelName, CancellationToken ct)
    {
        throw new NotSupportedException();
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection)
    {
        throw new NotSupportedException();
    }
}
