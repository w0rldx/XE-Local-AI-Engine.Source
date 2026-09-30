namespace XE_Local_AI_Engine.Client.Testing.Fakes;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     In-memory <see cref="INodeSettingsStore" /> that applies <see cref="UpdateAsync" /> the way the real store does:
///     the mutation runs against the record as it is AT WRITE TIME, not against whatever the caller loaded earlier.
/// </summary>
/// <remarks>
///     The optional sibling write models a concurrent whole-file writer landing between a caller's load and its write;
///     it is applied inside <see cref="UpdateAsync" /> just before the caller's mutation. Only the locking is modelled:
///     the real store's <c>Normalize</c> clamping and null-mutation guard are not, so a test that depends on either
///     uses the real <c>NodeSettingsStore</c>.
/// </remarks>
public sealed class FakeNodeSettingsStore : INodeSettingsStore
{
    private readonly Func<StoredNodeSettings, StoredNodeSettings>? _siblingWriteBeforeTheUpdate;

    public FakeNodeSettingsStore(StoredNodeSettings initial,
        Func<StoredNodeSettings, StoredNodeSettings>? siblingWriteBeforeTheUpdate = null)
    {
        _siblingWriteBeforeTheUpdate = siblingWriteBeforeTheUpdate;
        Current = initial;
    }

    /// <summary>The record the store currently holds.</summary>
    public StoredNodeSettings Current { get; private set; }

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

        if (_siblingWriteBeforeTheUpdate is not null)
        {
            Current = _siblingWriteBeforeTheUpdate(Current);
        }

        await SaveAsync(mutate(Current), cancellationToken);
        return Current;
    }
}
