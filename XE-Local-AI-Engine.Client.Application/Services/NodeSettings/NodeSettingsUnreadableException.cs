namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Thrown by <see cref="INodeSettingsStore.UpdateAsync" /> when <c>node-settings.json</c> is PRESENT but cannot be
///     read (truncated, hand-corrupted, or unreadable). The read-modify-write would otherwise load a default record over
///     the unreadable bytes and persist it as valid — silently "healing" corruption into a settings file that has lost
///     every stored value, the node's external-access posture included. Recovery is an explicit operator action: repair
///     or delete the file. Automatic callers must tolerate this without failing host start.
///     <para>
///         The message is the operator-facing text: <c>DomainValidationExceptionHandler</c> projects it straight through
///         to the 400 problem response, so it names both the path and the recovery.
///     </para>
///     Derives from <see cref="InvalidOperationException" /> because the store's state, not the caller's argument, is
///     what makes the operation impossible.
/// </summary>
public sealed class NodeSettingsUnreadableException(string settingsPath)
    : InvalidOperationException($"The node settings file at '{settingsPath}' could not be read, so nothing was written. Repair or delete node-settings.json and try again.")
{
    /// <summary>The absolute path of the unreadable <c>node-settings.json</c>.</summary>
    public string SettingsPath { get; } = settingsPath;
}
