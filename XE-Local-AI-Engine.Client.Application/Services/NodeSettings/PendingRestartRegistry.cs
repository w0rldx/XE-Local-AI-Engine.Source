namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Names the restart-gated settings whose effective value differs from the one this process started with.
/// </summary>
/// <remarks>
///     Compared on read against the record the host read before its container existed, so every write path counts (the
///     settings page, MCP, a hand-edited file) and a save that restores the boot value clears the field. A switch compares
///     stored-else-seed, so saving the seed over an unset value is no change. Immutable, so thread-safe.
/// </remarks>
public sealed class PendingRestartRegistry
{
    private readonly StoredNodeSettings _bootSettings;
    private readonly Func<StoredNodeSettings, NodeSettingsEffectiveValues> _resolveEffective;

    /// <param name="bootSettings">The stored record as the host read it at start.</param>
    /// <param name="resolveEffective">Stored record to effective values (stored, else the configuration seed).</param>
    public PendingRestartRegistry(StoredNodeSettings bootSettings, Func<StoredNodeSettings, NodeSettingsEffectiveValues> resolveEffective)
    {
        ArgumentNullException.ThrowIfNull(bootSettings);
        ArgumentNullException.ThrowIfNull(resolveEffective);
        _bootSettings = bootSettings;
        _resolveEffective = resolveEffective;
    }

    /// <summary>The gated wire field names whose value in <paramref name="current" /> differs from the boot value, in ordinal order.</summary>
    public IReadOnlyList<string> GetPendingFields(StoredNodeSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var bootEffective = _resolveEffective(_bootSettings);
        var currentEffective = _resolveEffective(current);
        return
        [
            .. NodeSettingsRestartGate.StoredValues
                                      .Where(field => !Equals(field.Value(_bootSettings, bootEffective), field.Value(current, currentEffective)))
                                      .Select(field => field.Key)
                                      .Order(StringComparer.Ordinal)
        ];
    }
}
