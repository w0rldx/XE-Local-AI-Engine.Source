namespace XE_Local_AI_Engine.Client.Services.Inference;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Default <see cref="IMachineKeyProvider" />: reads <see cref="StoredNodeSettings.MachineKey" /> and caches the
///     key the store actually holds for the process lifetime. Registered as a singleton.
/// </summary>
/// <remarks>
///     When the key is absent it mints a fresh <see cref="Guid" /> (<c>"N"</c> format) through
///     <see cref="INodeSettingsStore.UpdateAsync" />, preserving every other setting and adopting a key a racing
///     writer minted first rather than overwriting it. Generate-once is serialized so two concurrent first-callers
///     cannot mint two different keys. The key is LOCAL-ONLY and never emitted in telemetry, aggregates or logs.
/// </remarks>
public sealed class MachineKeyProvider : IMachineKeyProvider, IDisposable
{
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private readonly INodeSettingsStore _settingsStore;
    private volatile string? _cachedKey;

    public MachineKeyProvider(INodeSettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <inheritdoc />
    public async Task<string> GetMachineKeyAsync(CancellationToken ct)
    {
        var cached = _cachedKey;
        if (cached is not null)
        {
            return cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Re-check under the gate: a racing first-caller may have generated and cached it while we waited.
            if (_cachedKey is not null)
            {
                return _cachedKey;
            }

            var settings = await _settingsStore.LoadAsync(ct);
            var key = settings.MachineKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                // Mint through the store's read-modify-write: this gate serializes only THIS provider's callers, so UpdateAsync
                // re-reading under the store's lock is what makes everyone adopt the first key — a second key orphans every frozen profile, which is keyed by machine key.
                var persisted = await _settingsStore.UpdateAsync(latest => string.IsNullOrWhiteSpace(latest.MachineKey)
                        ? latest with
                        {
                            MachineKey = Guid.NewGuid().ToString("N")
                        }
                        : latest,
                    ct);
                key = persisted.MachineKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    // The store returns what it persisted, so this cannot happen against a real one — and caching a
                    // null would hand every profile lookup an empty machine key instead of failing.
                    throw new InvalidOperationException("The node settings store did not persist a machine key.");
                }
            }

            _cachedKey = key;
            return key;
        }
        finally
        {
            _gate.Release();
        }
    }
}
