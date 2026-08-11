namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

/// <summary>Provider tags for <see cref="LocalModelResponse.Provider" /> (chat picker grouping + egress hint).</summary>
public static class LocalModelProviders
{
    /// <summary>A node-local Ollama model (default; stays entirely on the node).</summary>
    public const string Ollama = "Ollama";

    /// <summary>A node-local GGUF model served by the bundled llama.cpp runtime (stays entirely on the node).</summary>
    public const string LlamaCpp = "llamacpp";

    /// <summary>A ChatGPT-subscription Codex cloud model (egress to the Codex backend).</summary>
    public const string CodexOAuth = "CodexOAuth";

    /// <summary>An Azure Foundry / Azure OpenAI deployment (egress to the configured Azure endpoint).</summary>
    public const string AzureFoundry = "AzureFoundry";
}

public sealed class ListLocalModelsResponse
{
    public required bool IsAvailable { get; init; }

    public string? SelectedModelName { get; init; }

    public string? ConfiguredDefaultModelName { get; init; }

    public string? Error { get; init; }

    public required IReadOnlyList<LocalModelResponse> Items { get; init; }
}

public sealed class GetLocalModelDetailsRequest
{
    public string? ModelName { get; init; }
}

public sealed class DeleteLocalModelRequest
{
    public string? ModelName { get; init; }
}

public sealed class SelectLocalModelRequest
{
    public string? ModelName { get; init; }
}

public sealed class LocalModelResponse
{
    public required string ModelName { get; init; }

    /// <summary>
    ///     The provider this model is served by: <c>"Ollama"</c> for a node-local model (the default), or
    ///     <c>"CodexOAuth"</c> for a ChatGPT-subscription Codex cloud model. The chat picker groups by this and shows
    ///     an egress hint for cloud models. Defaults to <c>"Ollama"</c> so existing local entries are unchanged.
    /// </summary>
    public string Provider { get; init; } = LocalModelProviders.Ollama;

    public long? SizeBytes { get; init; }

    public long? ModifiedAtUtc { get; init; }

    public string? Family { get; init; }

    public string? ParameterSize { get; init; }

    public string? QuantizationLevel { get; init; }

    public required bool IsSelected { get; init; }

    /// <summary>Effective classification (<c>override ?? detected</c>) as a <c>ModelKind</c> string.</summary>
    public required string Kind { get; init; }

    /// <summary>Machine-detected classification as a <c>ModelKind</c> string (drives the "reset to detected" affordance).</summary>
    public required string DetectedKind { get; init; }

    /// <summary>
    ///     Raw Ollama capability strings for read-only badges (e.g. <c>tools</c>, <c>vision</c>, <c>thinking</c>,
    ///     <c>native_reasoning</c>).
    /// </summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>
    ///     True when the model advertises the Ollama <c>thinking</c> capability — i.e. GRADED reasoning, a switchable
    ///     <c>think:&lt;level&gt;</c> control. The composer uses this to gate the graded reasoning-effort menu so a
    ///     non-reasoning model is never offered (or sent) the <c>think</c> field.
    /// </summary>
    public required bool IsReasoningCapable { get; init; }

    /// <summary>
    ///     True when the model reasons NATIVELY: its chat template bakes reasoning onto its own channel with no graded
    ///     switch (the OpenAI harmony family, e.g. gpt-oss). Mutually exclusive with <see cref="IsReasoningCapable" />.
    ///     The composer renders its own badge for this and keeps the BINARY on/none effort vocabulary — a native model
    ///     must never be routed into the graded path. Defaults to <see langword="false" /> so an older client that omits
    ///     the field behaves exactly as before.
    /// </summary>
    public bool IsNativeReasoningCapable { get; init; }

    /// <summary>
    ///     True when the model advertises the Ollama <c>tools</c> capability. The composer uses this to gate the local-tool
    ///     controls so a non-tool model is never offered tools.
    /// </summary>
    public required bool IsToolCapable { get; init; }

    /// <summary>
    ///     True when the model can accept image input (vision / multimodal) — its <c>mmproj</c> projector companion is
    ///     present locally, so llama-server is launched with <c>--mmproj</c>. The composer uses this to gate image
    ///     attachment so an image is never sent to a text-only model (which llama-server would reject). Defaults to
    ///     <see langword="false" /> so an older client that omits the field behaves exactly as before.
    /// </summary>
    public bool IsMultimodalCapable { get; init; }

    /// <summary>True when an operator override is set, so the effective kind differs from the detected one.</summary>
    public required bool IsOverridden { get; init; }
}

/// <summary>
///     Request DTO for set local model kind operations. <see cref="ModelName" /> is bound from the route; <see cref="Kind" />
///     is the desired <c>ModelKind</c> value (case-insensitive).
/// </summary>
public sealed class SetModelKindRequest
{
    public string? ModelName { get; init; }

    public string? Kind { get; init; }
}

/// <summary>
///     Request DTO for reset local model kind operations (clears the operator override). <see cref="ModelName" /> is
///     bound from the route.
/// </summary>
public sealed class ResetModelKindRequest
{
    public string? ModelName { get; init; }
}

public sealed class ModelKindResponse
{
    public required string ModelName { get; init; }

    public required string Kind { get; init; }

    public required string DetectedKind { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }

    public required bool IsOverridden { get; init; }
}

/// <summary>Request DTO for reading a model's extra-launch-argument override. <see cref="ModelName" /> is bound from the route.</summary>
public sealed class GetModelLaunchArgumentsRequest
{
    public string? ModelName { get; init; }
}

/// <summary>
///     Request DTO for setting a model's extra <c>llama-server</c> launch-argument override (developer/advanced).
///     <see cref="ModelName" /> is bound from the route; <see cref="RawArguments" /> is the raw operator-entered flag
///     string (for example <c>--top-k 40 --repeat-penalty 1.1</c>).
/// </summary>
public sealed class SetModelLaunchArgumentsRequest
{
    public string? ModelName { get; init; }

    public string? RawArguments { get; init; }
}

/// <summary>Response for the per-model extra-launch-argument override endpoints. Empty <see cref="RawArguments" /> means no override.</summary>
public sealed class ModelLaunchArgumentsResponse
{
    public required string ModelName { get; init; }

    public required string RawArguments { get; init; }
}

public sealed class LocalModelDetailsResponse
{
    public required string ModelName { get; init; }

    public int? MaxContextTokens { get; init; }

    /// <summary>
    ///     The effective context window (in tokens) the RUNNING llama.cpp process for this model actually loaded — the
    ///     launched <c>-c</c> as the server reports it via <c>/props</c> (AUD4-02). Null when no chat process is running
    ///     for the model or the runtime does not expose it. Distinct from <see cref="MaxContextTokens" /> (the model's
    ///     advertised train ceiling): the chat context-usage meter should size against this real window when present.
    /// </summary>
    public int? EffectiveContextTokens { get; init; }

    public string? Template { get; init; }

    public string? System { get; init; }

    public string? License { get; init; }
}

public sealed class SelectLocalModelResponse
{
    public required string SelectedModelName { get; init; }
}

public sealed class DeleteLocalModelResponse
{
    public required string ModelName { get; init; }

    public required bool Deleted { get; init; }
}

/// <summary>
///     Response for <c>GET models/running</c>: the models the runtime currently holds in memory. Mirrors the
///     availability/error shape of <see cref="ListLocalModelsResponse" /> so the page degrades gracefully when the
///     provider is unreachable (empty list, <see cref="IsAvailable" /> false).
/// </summary>
public sealed class RunningLocalModelsResponse
{
    public required bool IsAvailable { get; init; }

    /// <summary>
    ///     Whether the optional Ollama runtime is configured/enabled on this node (the <c>XE_OLLAMA_RUNTIME_ENABLED</c>
    ///     gate). When false the client can stop polling this endpoint entirely — no Ollama daemon will ever answer — so
    ///     it never backs off forever against a runtime that is switched off. Distinct from <see cref="IsAvailable" />,
    ///     which reflects whether a configured daemon is currently reachable.
    /// </summary>
    public required bool OllamaConfigured { get; init; }

    public string? Error { get; init; }

    public required IReadOnlyList<RunningLocalModelResponse> Items { get; init; }
}

/// <summary>A single loaded model, with its memory footprint and eviction time when the runtime reports them.</summary>
public sealed class RunningLocalModelResponse
{
    public required string ModelName { get; init; }

    /// <summary>Total resident size in bytes (RAM + VRAM); null when the runtime did not report it.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Portion resident in GPU VRAM in bytes; null when the runtime did not report it.</summary>
    public long? SizeVramBytes { get; init; }

    /// <summary>Scheduled eviction time as Unix epoch milliseconds (UTC); null when the runtime did not report it.</summary>
    public long? ExpiresAtUtc { get; init; }
}

/// <summary>Request DTO for <c>POST models/{modelName}/unload</c>. <see cref="ModelName" /> is bound from the route.</summary>
public sealed class UnloadLocalModelRequest
{
    public string? ModelName { get; init; }
}

/// <summary>Response for a graceful in-memory unload. Idempotent: a model that was not loaded still reports success.</summary>
public sealed class UnloadLocalModelResponse
{
    public required string ModelName { get; init; }

    public required bool Unloaded { get; init; }
}
