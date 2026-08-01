namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Keeps the persisted <c>AgentHome:ToolCapableModels</c> allow-list in step with what the installed GGUF models
///     actually advertise, so a model the app itself recommended and downloaded can drive tool calls without the operator
///     having to discover and hand-type its name.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Why this exists.</strong> Tool calling is gated on exact membership of that allow-list
///         (<c>LocalToolOfferProvider.IsToolCapable</c>), whose shipped default was a two-entry list of models from an
///         earlier generation (<c>qwen3:8b</c>, a Qwen2.5-3B GGUF). Every current model the advisor ranks and downloads —
///         including tool-capable ones — was absent, so a user who followed the app's own recommendation got no tool
///         calling and no explanation. Meanwhile the capability was ALREADY known: <c>GgufCapabilityDetector</c>
///         classifies it deterministically from the GGUF's embedded Jinja chat template and the result is already
///         persisted on every installed model as <c>LocalModelDescriptor.IsToolCapable</c>. Nothing consumed it.
///     </para>
///     <para>
///         <strong>Why the allow-list is fed rather than replaced.</strong> The gate is synchronous and on the per-turn
///         offer path, while capability resolution is an async store read — and, more importantly, the allow-list is the
///         operator-visible source of truth: it is an editable field in Node Settings (<c>node-settings-tool-capable-models</c>)
///         and is what the Agents page displays. Writing detection results INTO it keeps one inspectable, auditable list
///         that an operator can still curate, instead of adding a second invisible capability path that silently
///         disagrees with the UI. The gate itself is unchanged.
///     </para>
///     <para>
///         <strong>Additive only.</strong> Neither method ever removes a name. An operator who added a model by hand — or
///         one served by Ollama/a cloud provider, which have no GGUF descriptor at all — keeps it. Detection can only
///         ever grant capability here, never revoke it.
///     </para>
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
    ///     added. Runs once at startup so models installed BEFORE this behaviour existed are corrected too — without it
    ///     the fix would only ever apply to future downloads, leaving every already-installed model still silently
    ///     tool-less.
    /// </summary>
    Task<int> BackfillInstalledAsync(CancellationToken cancellationToken = default);
}
