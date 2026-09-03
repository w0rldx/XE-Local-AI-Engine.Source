namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

/// <summary>
///     Process-local cancellation handles keyed by execution id.
///     <para>
///         It exists because <c>IInvocationRunner.Cancel</c> is not enough on its own: the lifecycle tracker only
///         cancels the run it is CURRENTLY driving, so a row still waiting on the node's invocation lease would ignore
///         it. The coordinator registers a token here before it waits, and every cancel path signals this registry as
///         well as the runner.
///     </para>
/// </summary>
internal sealed class IntegrationCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _entries = new();

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "A successful registration transfers CancellationTokenSource ownership to Remove; a losing one disposes it here.")]
    public bool TryRegister(Guid executionId, out CancellationToken token)
    {
        var source = new CancellationTokenSource();
        if (_entries.TryAdd(executionId, source))
        {
            token = source.Token;
            return true;
        }

        source.Dispose();
        token = default;
        return false;
    }

    /// <summary>
    ///     Requests cancellation. Returns <see langword="false" /> when nothing is registered — which is not an error:
    ///     the durable stop marker is what a restart reads, and this is only the in-process shortcut.
    /// </summary>
    public bool Signal(Guid executionId)
    {
        if (!_entries.TryGetValue(executionId, out var source))
        {
            return false;
        }

        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The run finished and unregistered between the lookup and the cancel. Nothing to stop.
            return false;
        }
        catch (AggregateException)
        {
            // A registered callback threw. Cancellation was still requested for every one of them, and the durable
            // marker — not this call — is what finalises the row.
            return true;
        }
    }

    public void Remove(Guid executionId)
    {
        if (_entries.TryRemove(executionId, out var source))
        {
            source.Dispose();
        }
    }
}
