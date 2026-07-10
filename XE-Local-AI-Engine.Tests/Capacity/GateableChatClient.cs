namespace XE_Local_AI_Engine.Tests.Capacity;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

/// <summary>
///     A fake <see cref="IChatClient" /> for the spawn tests. Returns a fixed assistant response and:
///     counts calls; records whether the inner run observed cancellation; optionally delays (cancellably) before
///     responding; and optionally HOLDS the run open on a caller-supplied gate so a test can keep a sub-agent "live"
///     (to exercise the concurrent fan-out cap) and observe when it started.
/// </summary>
internal sealed class GateableChatClient : IChatClient
{
    private readonly string _responseText;
    private readonly TimeSpan _delay;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _hold;

    public GateableChatClient(string responseText = "sub-agent-result", TimeSpan? delayBeforeResponse = null)
    {
        _responseText = responseText;
        _delay = delayBeforeResponse ?? TimeSpan.Zero;
    }

    public int CallCount { get; private set; }

    public bool InnerObservedCancellation { get; private set; }

    /// <summary>Holds every run open until <paramref name="gate" /> completes (used to keep a spawn live during a fan-out test).</summary>
    public void HoldUntil(Task gate)
    {
        _hold = gate;
    }

    /// <summary>Completes once a run has started (so a test can assert one spawn is live before launching a second).</summary>
    public Task WaitUntilRunningAsync()
    {
        return _started.Task;
    }

    /// <summary>The tool names the inner agent passed on the last run (read from <c>ChatOptions.Tools</c>), for tool-set assertions.</summary>
    public IReadOnlyList<string> LastToolNames { get; private set; } = [];

    /// <summary>The model id the inner agent passed on the last run (read from <c>ChatOptions.ModelId</c>) — pins that a spawn binds its model so RuntimeChatClient routes to the right provider.</summary>
    public string? LastModelId { get; private set; }

    /// <summary>The system instructions the inner agent passed on the last run (read from <c>ChatOptions.Instructions</c>).</summary>
    public string? LastInstructions { get; private set; }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        CaptureTools(options);
        await RunBodyAsync(cancellationToken).ConfigureAwait(false);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, _responseText));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        CaptureTools(options);
        await RunBodyAsync(cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, _responseText);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
    }

    public void Dispose()
    {
        // Nothing to release.
    }

    private void CaptureTools(ChatOptions? options)
    {
        LastModelId = options?.ModelId;
        LastInstructions = options?.Instructions;
        LastToolNames = options?.Tools is { } tools
            ? [.. tools.Select(static tool => tool.Name)]
            : [];
    }

    private async Task RunBodyAsync(CancellationToken cancellationToken)
    {
        _started.TrySetResult();

        try
        {
            if (_hold is not null)
            {
                await _hold.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            InnerObservedCancellation = true;
            throw;
        }

        InnerObservedCancellation = cancellationToken.IsCancellationRequested;
        cancellationToken.ThrowIfCancellationRequested();
    }
}
