namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Thrown by <see cref="INodeSettingsStore.UpdateAsync" /> when <c>node-settings.json</c> is PRESENT but cannot be
///     read, whether truncated, hand-corrupted or otherwise unreadable.
/// </summary>
/// <remarks>
///     The read-modify-write would otherwise load a default record over the unreadable bytes and persist it as valid,
///     silently "healing" corruption into a file that has lost every stored value, the node's external-access posture
///     included. Recovery is an explicit operator action — repair or delete the file — and automatic callers must
///     tolerate this without failing host start. The message is the operator-facing text that
///     <c>DomainValidationExceptionHandler</c> projects into the 400 response, naming both the path and the recovery.
/// </remarks>
public sealed class NodeSettingsUnreadableException : InvalidOperationException
{
    public NodeSettingsUnreadableException(string settingsPath) : base($"The node settings file at '{settingsPath}' could not be read, so nothing was written. Repair or delete node-settings.json and try again.")
    {
        SettingsPath = settingsPath;
    }

    /// <summary>The absolute path of the unreadable <c>node-settings.json</c>.</summary>
    public string SettingsPath { get; }
}
