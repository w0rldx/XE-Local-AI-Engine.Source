namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>Provider tags for <see cref="LocalModelResponse.Provider" /> (chat picker grouping + egress hint).</summary>
public static class LocalModelProviders
{
    /// <summary>A node-local GGUF model served by the bundled llama.cpp runtime (stays entirely on the node).</summary>
    public const string LlamaCpp = "llamacpp";

    /// <summary>A ChatGPT-subscription Codex cloud model (egress to the Codex backend).</summary>
    public const string CodexOAuth = "CodexOAuth";

    /// <summary>An Azure Foundry / Azure OpenAI deployment (egress to the configured Azure endpoint).</summary>
    public const string AzureFoundry = "AzureFoundry";

    /// <summary>
    ///     A model on an operator-registered external OpenAI-compatible connection — ONE tag for every connection,
    ///     because the provider is a multiplexer and the connection is carried by the id itself.
    /// </summary>
    /// <remarks>
    ///     Egress and grouping are therefore read from <see cref="LocalModelResponse.DeclaredLocality" /> and
    ///     <see cref="LocalModelResponse.ExternalConnectionId" />, never from this tag alone.
    /// </remarks>
    public const string External = "external";
}

/// <summary>
///     The operator-DECLARED trust locality of an external connection, as it appears on
///     <see cref="LocalModelResponse.DeclaredLocality" />. Lowercase wire values so the client can compare them
///     without case handling.
/// </summary>
public static class LocalModelDeclaredLocalities
{
    /// <summary>Self-hosted on the node or its trusted network: full local-parity tool/knowledge privileges.</summary>
    public const string Local = "local";

    /// <summary>A hosted endpoint outside the trust boundary: gated exactly like the built-in cloud providers.</summary>
    public const string Cloud = "cloud";
}

public sealed class ListLocalModelsResponse
{
    public string? SelectedModelName { get; init; }

    public string? ConfiguredDefaultModelName { get; init; }

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
    ///     The provider this model is served by, one of <see cref="LocalModelProviders" />.
    /// </summary>
    /// <remarks>The chat picker groups by this and shows an egress hint for cloud models.</remarks>
    public required string Provider { get; init; }

    /// <summary>
    ///     A friendly label the operator gave this model, or <see langword="null" /> when they gave none (the client
    ///     then falls back to <see cref="ModelName" />). Populated for external models and for Azure Foundry
    ///     deployments; null for every locally-installed model, whose name IS its identity.
    /// </summary>
    public string? DisplayLabel { get; init; }

    /// <summary>
    ///     The external connection serving this model, or <see langword="null" /> for every non-external entry. What
    ///     makes per-connection picker sections possible: <c>provider: "external"</c> alone cannot say which of the
    ///     operator's endpoints a model belongs to.
    /// </summary>
    public string? ExternalConnectionId { get; init; }

    /// <summary>The serving connection's operator-chosen label, for the section heading and the "Sent to …" egress cue.</summary>
    public string? ExternalConnectionName { get; init; }

    /// <summary>
    ///     The operator-declared trust locality of the serving connection (<see cref="LocalModelDeclaredLocalities" />),
    ///     or <see langword="null" /> for every non-external entry.
    /// </summary>
    /// <remarks>
    ///     This is a DECLARATION, never an inference from the base URL, and it is what decides whether the model is
    ///     grouped and badged as local or as cloud.
    /// </remarks>
    public string? DeclaredLocality { get; init; }

    public long? SizeBytes { get; init; }

    public long? ModifiedAtUtc { get; init; }

    public string? Family { get; init; }

    public string? ParameterSize { get; init; }

    public string? QuantizationLevel { get; init; }

    /// <summary>Typed acquisition provenance for installed GGUF models; null for legacy and cloud entries.</summary>
    public LocalModelOrigin? Origin { get; init; }

    /// <summary>Verified aggregate identity of installed GGUF weight/projector content.</summary>
    public string? ModelContentFingerprint { get; init; }

    public required bool IsSelected { get; init; }

    /// <summary>Effective classification (<c>override ?? detected</c>) as a <c>ModelKind</c> string.</summary>
    public required string Kind { get; init; }

    /// <summary>Machine-detected classification as a <c>ModelKind</c> string (drives the "reset to detected" affordance).</summary>
    public required string DetectedKind { get; init; }

    /// <summary>
    ///     Raw capability strings for read-only badges (e.g. <c>tools</c>, <c>vision</c>, <c>thinking</c>,
    ///     <c>native_reasoning</c>).
    /// </summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>
    ///     True when the model advertises the <c>thinking</c> capability — GRADED reasoning, a switchable
    ///     <c>think:&lt;level&gt;</c> control.
    /// </summary>
    /// <remarks>
    ///     The composer uses this to gate the graded reasoning-effort menu, so a non-reasoning model is never offered
    ///     (or sent) the <c>think</c> field.
    /// </remarks>
    public required bool IsReasoningCapable { get; init; }

    /// <summary>
    ///     True when the model reasons NATIVELY: its chat template bakes reasoning onto its own channel with no graded
    ///     switch (the OpenAI harmony family, e.g. gpt-oss).
    /// </summary>
    /// <remarks>
    ///     Mutually exclusive with <see cref="IsReasoningCapable" />. The composer renders its own badge for this and
    ///     keeps the BINARY on/none effort vocabulary — a native model must never be routed into the graded path.
    ///     Defaults to <see langword="false" />, which is what a client omitting the field means.
    /// </remarks>
    public bool IsNativeReasoningCapable { get; init; }

    /// <summary>
    ///     Whether a model that reasons also accepts a GRADED effort level, or only reasons or does not.
    /// </summary>
    /// <remarks>
    ///     Distinguishes the two shapes <see cref="IsReasoningCapable" /> alone conflates for an externally served
    ///     model: an endpoint honoring <c>reasoning_effort</c> gets the graded selector, one that merely reasons gets
    ///     the binary control, since offering levels it ignores is a menu whose entries do nothing.
    ///     <see langword="null" /> means "not declared", which is every non-external entry: a local model's graded
    ///     control follows its <c>thinking</c> capability and a cloud provider's its own, neither of which this field may change.
    /// </remarks>
    public bool? IsReasoningEffortCapable { get; init; }

    /// <summary>
    ///     True when llama.cpp can ENFORCE a per-request thinking budget for this model.
    /// </summary>
    /// <remarks>
    ///     Its chat template renders a literal reasoning end marker (<c>&lt;/think&gt;</c>, gemma-4's
    ///     <c>&lt;channel|&gt;</c>, …), which is what llama-server turns into the non-empty think-end-tag set its
    ///     <c>reasoning_budget_tokens</c> gate requires. When <see langword="false" /> alongside
    ///     <see cref="IsReasoningCapable" />, the graded effort still applies but its token cap does NOT: the server
    ///     would accept the field and silently ignore it, so the node omits it. Defaults to <see langword="true" />.
    /// </remarks>
    public bool ReasoningBudgetEnforceable { get; init; } = true;

    /// <summary>
    ///     True when the model advertises the <c>tools</c> capability. The composer uses this to gate the local-tool
    ///     controls so a non-tool model is never offered tools.
    /// </summary>
    public required bool IsToolCapable { get; init; }

    /// <summary>
    ///     True when the model can accept image input (vision / multimodal) — its <c>mmproj</c> projector companion is
    ///     present locally, so llama-server is launched with <c>--mmproj</c>.
    /// </summary>
    /// <remarks>
    ///     The composer uses this to gate image attachment, so an image is never sent to a text-only model (which
    ///     llama-server would reject). Defaults to <see langword="false" />.
    /// </remarks>
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
    ///     launched <c>-c</c> as the server reports it via <c>/props</c>.
    /// </summary>
    /// <remarks>
    ///     Null when no chat process is running for the model, or the runtime does not expose it. Distinct from
    ///     <see cref="MaxContextTokens" /> (the model's advertised train ceiling): the chat context-usage meter sizes
    ///     against this real window when present.
    /// </remarks>
    public int? EffectiveContextTokens { get; init; }

    /// <summary>
    ///     Whether the RUNNING llama.cpp process keeps this Mixture-of-Experts model's expert weights in system RAM (expert offload)
    ///     rather than on the GPU; null when no chat process is running for the model.
    /// </summary>
    public bool? ExpertsOffloaded { get; init; }

    /// <summary>The operator's friendly label for this model, or null when they gave none. Populated for external models.</summary>
    /// <remarks>
    ///     The four fields below mirror their namesakes on <see cref="LocalModelResponse" /> deliberately: a details
    ///     view reached directly (a deep link, a reload) has no list entry to read the connection identity from, and
    ///     two different spellings of the same four facts is how the picker and the details pane come to disagree.
    /// </remarks>
    public string? DisplayLabel { get; init; }

    /// <summary>The external connection serving this model, or null for every non-external entry.</summary>
    public string? ExternalConnectionId { get; init; }

    /// <summary>The serving connection's operator-chosen label.</summary>
    public string? ExternalConnectionName { get; init; }

    /// <summary>The declared trust locality (<see cref="LocalModelDeclaredLocalities" />), or null for a non-external entry.</summary>
    public string? DeclaredLocality { get; init; }

    /// <summary>
    ///     Whether a reasoning model also accepts a graded effort level; null when undeclared. Mirrors
    ///     <see cref="LocalModelResponse.IsReasoningEffortCapable" /> for the same reason the four fields above are
    ///     mirrored: a details view reached directly has no list entry to read it from.
    /// </summary>
    public bool? IsReasoningEffortCapable { get; init; }

    /// <summary>Typed acquisition provenance for an installed GGUF; null for legacy and non-GGUF models.</summary>
    public LocalModelOrigin? Origin { get; init; }

    /// <summary>Verified aggregate identity of installed GGUF weight/projector content.</summary>
    public string? ModelContentFingerprint { get; init; }

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
