namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Names the restart-gated settings whose stored value differs from the one this process started with.
/// </summary>
/// <remarks>
///     Compared on read against the record the host read before its container existed, so every write path counts (the
///     settings page, MCP, a hand-edited file) and a save that restores the boot value clears the field. Immutable, so
///     thread-safe; the process lifetime is its lifetime.
/// </remarks>
public sealed class PendingRestartRegistry
{
    private readonly StoredNodeSettings _bootSettings;

    /// <param name="bootSettings">The stored record as the host read it at start.</param>
    public PendingRestartRegistry(StoredNodeSettings bootSettings)
    {
        ArgumentNullException.ThrowIfNull(bootSettings);
        _bootSettings = bootSettings;
    }

    /// <summary>The gated wire field names whose value in <paramref name="current" /> differs from the boot value, in ordinal order.</summary>
    public IReadOnlyList<string> GetPendingFields(StoredNodeSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return
        [
            .. NodeSettingsRestartGate.StoredValues
                                      .Where(field => !Equals(field.Value(_bootSettings), field.Value(current)))
                                      .Select(field => field.Key)
                                      .Order(StringComparer.Ordinal)
        ];
    }
}
