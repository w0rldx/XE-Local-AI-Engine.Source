namespace XE_Local_AI_Engine.Client.Services.Capacity;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Default <see cref="ISpawnSerializer" />. Lazily creates one <see cref="SemaphoreSlim" />(1,1) per
///     <c>(model, role)</c> key in a <see cref="ConcurrentDictionary{TKey,TValue}" /> and serializes same-model spawn
///     runs against it with a bounded, cancellable wait. Process-wide singleton.
/// </summary>
public sealed class SpawnSerializer : ISpawnSerializer
{
    // One serialization gate per (model, role). Created on first use; never removed (a node has a small, bounded set of
    // installed models, so the map stays tiny and a cached semaphore avoids a create/dispose race on a hot path).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<string> RunSerializedAsync(string modelName,
        ModelRole role,
        TimeSpan timeout,
        Func<CancellationToken, Task<string>> run,
        Func<string> onTimeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(onTimeout);

        var gate = _gates.GetOrAdd(BuildKey(modelName, role), static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));

        // Bounded wait: a queued same-model spawn that cannot get its turn within the timeout returns a sanitized
        // "busy" result instead of blocking the parent tool call indefinitely. A cancelled parent surfaces as OCE.
        var acquired = await gate.WaitAsync(timeout, ct).ConfigureAwait(false);
        if (!acquired)
        {
            return onTimeout();
        }

        try
        {
            return await run(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string BuildKey(string modelName, ModelRole role)
    {
        return $"{modelName}|{(int)role}";
    }
}
