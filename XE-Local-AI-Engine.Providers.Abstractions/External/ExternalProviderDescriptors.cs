namespace XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The non-secret read model of one external OpenAI-compatible API connection. Deliberately carries NO API key:
///     the key is a secret whose only legitimate consumer is the transport, and it reaches the transport bound to its
///     own endpoint through <see cref="IExternalProviderRegistry.TryResolveTransportBindingAsync" />, so every
///     catalog/UI/policy consumer of this descriptor is structurally incapable of leaking it.
/// </summary>
public sealed record ExternalProviderConnectionDescriptor
{
    /// <summary>
    ///     The immutable connection slug — the first segment of every <c>ext:</c> model id this connection owns.
    ///     Canonicalized at write time to <see cref="ExternalModelId.ConnectionIdPattern" />.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Operator-chosen label shown in the picker, egress cues, and usage attribution.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    ///     The NORMALIZED, <c>/v1</c>-terminated base address (see the transport's base-URL normalizer). Both the
    ///     connect-time probe and every chat send are pinned to it, so a descriptor carrying an un-normalized value
    ///     would silently widen the outbound guard.
    /// </summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>The operator-declared trust locality driving every downstream tool/knowledge/dev-mode gate.</summary>
    public required ExternalProviderLocality Locality { get; init; }

    /// <summary>
    ///     Per-connection network timeout, or <see langword="null" /> to use the transport default. A slow self-hosted
    ///     runtime legitimately needs minutes for a long generation, so this is an outer floor, never a per-token bound.
    /// </summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>
///     One operator-registered model on an external connection. Every capability here is DECLARED, never probed: only
///     <c>POST /v1/chat/completions</c> is universal across OpenAI-compatible servers, and no endpoint advertises tool,
///     vision or reasoning support in a way that can be trusted across llama.cpp / vLLM / LM Studio / hosted APIs.
/// </summary>
public sealed record ExternalProviderModelDescriptor
{
    /// <summary>The backing model id sent on the wire as the request's <c>model</c> field, verbatim.</summary>
    public required string WireId { get; init; }

    /// <summary>Optional friendly label; consumers fall back to <see cref="WireId" /> when it is absent.</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    ///     The declared context window in tokens, or <see langword="null" /> when the operator did not declare one (the
    ///     turn budgeter then keeps its conservative fallback rather than assuming a window the server may not have).
    /// </summary>
    public int? ContextLength { get; init; }

    public bool SupportsTools { get; init; }

    public bool SupportsVision { get; init; }

    public bool SupportsReasoning { get; init; }

    /// <summary>
    ///     Whether the endpoint honours a top-level <c>reasoning_effort</c> body field. Independent of
    ///     <see cref="SupportsReasoning" /> in principle, but only read alongside it: a model that reasons without a
    ///     graded switch gets binary on/off semantics and no effort field on the wire.
    /// </summary>
    public bool SupportsReasoningEffort { get; init; }

    /// <summary>
    ///     The effort applied when the turn selects none, in the canonical lowercase vocabulary
    ///     (<c>none</c>/<c>on</c>/<c>minimal</c>/<c>low</c>/<c>medium</c>/<c>high</c>/<c>xhigh</c>). A string rather
    ///     than an enum because that vocabulary's single source of truth (the chat reasoning normalizer) lives in the
    ///     application layer, which this seam must not reference. Unrecognized values are treated as unspecified.
    /// </summary>
    public string? DefaultReasoningEffort { get; init; }
}

/// <summary>
///     A resolved <c>ext:{connectionId}/{wireId}</c> identity: the connection that serves the model plus the model's own
///     declarations. The single value every consumer needs to route, gate, or render one external model.
/// </summary>
/// <param name="Connection">The connection serving <paramref name="Model" />.</param>
/// <param name="Model">The registered model's declarations.</param>
public sealed record ExternalProviderModelRegistration(ExternalProviderConnectionDescriptor Connection, ExternalProviderModelDescriptor Model)
{
    /// <summary>The canonical namespaced model id this registration is addressed by.</summary>
    public string ModelId => ExternalModelId.Format(Connection.Id, Model.WireId);
}
