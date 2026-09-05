namespace XE_Local_AI_Engine.Tests.Capacity;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.External;

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

    /// <summary>The executable tools from the last run, retained so integration tests can invoke a resolved adapter.</summary>
    public IReadOnlyList<AITool> LastTools { get; private set; } = [];

    /// <summary>The model id the inner agent passed on the last run (read from <c>ChatOptions.ModelId</c>) — pins that a spawn binds its model so RuntimeChatClient routes to the right provider.</summary>
    public string? LastModelId { get; private set; }

    /// <summary>The external binding pin in force for the last run's model, or <see langword="null" /> when that send was unpinned.</summary>
    public ExternalProviderBindingPin? LastBindingPin { get; private set; }

    /// <summary>The system instructions the inner agent passed on the last run (read from <c>ChatOptions.Instructions</c>).</summary>
    public string? LastInstructions { get; private set; }

    /// <summary>The additional properties the inner agent passed on the last run (read from <c>ChatOptions.AdditionalProperties</c>) — carries the Ollama <c>think</c> reasoning option + the Codex reasoning-effort side channel.</summary>
    public AdditionalPropertiesDictionary? LastAdditionalProperties { get; private set; }

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
        // Read INSIDE the run, which is where the real transport reads it. The pin is ambient, so this is the only
        // place a test can see whether the child's own send was actually covered by one.
        LastBindingPin = ExternalProviderBindingPinScope.Find(options?.ModelId);
        LastModelId = options?.ModelId;
        LastInstructions = options?.Instructions;
        LastAdditionalProperties = options?.AdditionalProperties;
        LastTools = options?.Tools is { } executableTools ? [.. executableTools] : [];
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
                // real-timer: this latency IS the subject's input — callers use it to keep a send in flight while a
                // cancellation or timeout races it. The hold TaskCompletionSource above is the deterministic seam for
                // every other case; a caller that only needs "still running" should use that instead.
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
