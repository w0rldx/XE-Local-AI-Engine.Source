namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

/// <summary>
///     A minimal in-memory <see cref="IChatClient" /> for routing/wiring tests. Records the last <see cref="ChatOptions" />
///     it received so callers can assert what a decorating client passed through, and returns a fixed response.
/// </summary>
internal sealed class StubChatClient : IChatClient
{
    private readonly Func<Task>? _midStreamGate;
    private readonly ChatResponse _response;

    /// <param name="responseText">Text of the fixed response.</param>
    /// <param name="midStreamGate">
    ///     Optional hook awaited BETWEEN streamed updates so a test can hold a stream open in-flight (e.g. to force a
    ///     selection swap while it is enumerating). After awaiting it, the stream checks <see cref="IsDisposed" /> and
    ///     throws <see cref="ObjectDisposedException" /> if it was disposed underneath the open enumeration — so a
    ///     use-after-dispose would surface as a real failure. When null (default), streaming completes immediately.
    /// </param>
    public StubChatClient(string responseText = "ok", Func<Task>? midStreamGate = null)
    {
        _response = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText));
        _midStreamGate = midStreamGate;
    }

    public ChatOptions? LastOptions { get; private set; }

    public int CallCount { get; private set; }

    public bool IsDisposed { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        CallCount++;
        return Task.FromResult(_response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        CallCount++;

        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");

        if (_midStreamGate is not null)
        {
            // Hold the stream open here so the test can flip the selection (which would dispose a swapped-out
            // client if the production code disposed on swap) WHILE this enumeration is live.
            await _midStreamGate().ConfigureAwait(false);

            // If a disposal happened underneath us, surface it as the failure the regression guards against.
            ObjectDisposedException.ThrowIf(IsDisposed, this);
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
    }

    public void Dispose()
    {
        IsDisposed = true;
    }
}
