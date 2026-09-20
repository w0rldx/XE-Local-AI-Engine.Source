namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Keeps the persisted <c>AgentHome:ToolCapableModels</c> allow-list in step with what the installed GGUF models
///     actually advertise, so a model the app recommended and downloaded can drive tool calls unaided.
/// </summary>
/// <remarks>
///     Tool calling is gated on exact membership of that list (<c>LocalToolOfferProvider.IsToolCapable</c>); the capability itself is already
///     persisted per model as <c>LocalModelDescriptor.IsToolCapable</c> by <c>GgufCapabilityDetector</c>. ADDITIVE ONLY — neither method ever
///     removes a name, so a hand-added model, or one served by Ollama or a cloud provider (no GGUF descriptor), keeps it: detection can grant
///     capability here, never revoke it. Why the list is fed rather than replaced, and what the detector reads:
///     docs/wiki/08-data-and-persistence.md ("The tool-capable model allow-list is fed, never replaced").
/// </remarks>
public interface IToolCapableModelRegistrar
{
    /// <summary>
    ///     Adds <paramref name="modelName" /> to the persisted allow-list when its installed GGUF descriptor reports
    ///     tool capability. Returns <see langword="true" /> only when a name was actually added (a model that is already
    ///     listed, not installed, or not tool-capable is a no-op).
    /// </summary>
    Task<bool> RegisterIfToolCapableAsync(string modelName, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Adds every installed, tool-capable GGUF that is missing from the allow-list, and returns how many names were
    ///     added.
    /// </summary>
    /// <remarks>
    ///     Runs once at startup so models installed BEFORE this behaviour existed are corrected too; without it the fix
    ///     would only ever apply to future downloads, leaving every already-installed model still silently tool-less.
    /// </remarks>
    Task<int> BackfillInstalledAsync(CancellationToken cancellationToken = default);
}
