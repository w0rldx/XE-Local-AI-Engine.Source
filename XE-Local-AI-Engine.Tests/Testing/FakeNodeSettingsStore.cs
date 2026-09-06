namespace XE_Local_AI_Engine.Tests.Testing;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     In-memory <see cref="INodeSettingsStore" /> that applies <see cref="UpdateAsync" /> the way the real store does:
///     the mutation runs against the record as it is AT WRITE TIME, not against whatever the caller loaded earlier.
/// </summary>
/// <remarks>
///     <paramref name="siblingWriteBeforeTheUpdate" /> models the concurrent whole-file writer — a machine-key mint, a
///     default-model selection, a settings save from the UI — that lands between a caller's load and its write. It is
///     applied to the stored record inside <see cref="UpdateAsync" /> immediately before the caller's mutation, which is
///     the interleaving a load-modify-save built from a stale record silently discards.
///     <para>
///         What this fake models is the LOCKING alone — the mutation running against the record the store currently
///         holds. It does not reproduce the real store's <c>Normalize</c> clamping or its null-mutation guard, so a
///         test that depends on either must use the real <c>NodeSettingsStore</c>.
///     </para>
/// </remarks>
internal sealed class FakeNodeSettingsStore(
    StoredNodeSettings initial,
    Func<StoredNodeSettings, StoredNodeSettings>? siblingWriteBeforeTheUpdate = null) : INodeSettingsStore
{
    /// <summary>The record the store currently holds.</summary>
    public StoredNodeSettings Current { get; private set; } = initial;

    /// <summary>The last record persisted, or <see langword="null" /> when nothing has been written.</summary>
    public StoredNodeSettings? Saved { get; private set; }

    /// <summary>How many times a write reached the file — the cache churn a no-change early return exists to avoid.</summary>
    public int WriteCount { get; private set; }

    public Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Current);
    }

    public StoredNodeSettings Load(CancellationToken cancellationToken = default)
    {
        return Current;
    }

    public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        Saved = settings;
        WriteCount++;
        return Task.CompletedTask;
    }

    public async Task<StoredNodeSettings> UpdateAsync(Func<StoredNodeSettings, StoredNodeSettings> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        if (siblingWriteBeforeTheUpdate is not null)
        {
            Current = siblingWriteBeforeTheUpdate(Current);
        }

        await SaveAsync(mutate(Current), cancellationToken).ConfigureAwait(false);
        return Current;
    }
}
