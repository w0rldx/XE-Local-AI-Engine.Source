namespace XE_Local_AI_Engine.Client.Services.Inference;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Default <see cref="IMachineKeyProvider" />. Reads <see cref="StoredNodeSettings.MachineKey" />; when it is absent
///     it generates a fresh <see cref="Guid" /> (<c>"N"</c> format), persists it back through
///     <see cref="INodeSettingsStore" /> (preserving every other setting via a <c>with</c> copy), and caches it for the
///     process lifetime. Generate-once is serialized so two concurrent first-callers cannot mint two different keys.
///     Registered as a singleton.
/// </summary>
/// <remarks>The key is LOCAL-ONLY and is never emitted in telemetry, aggregates, or logs.</remarks>
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

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate: a racing first-caller may have generated and cached it while we waited.
            if (_cachedKey is not null)
            {
                return _cachedKey;
            }

            var settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
            var key = settings.MachineKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                key = Guid.NewGuid().ToString("N");
                await _settingsStore.SaveAsync(settings with { MachineKey = key }, ct).ConfigureAwait(false);
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
