namespace XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The operator's whole external-provider configuration as the settings editor reads it.
/// </summary>
/// <remarks>
///     Carries <see cref="Revision" /> so an editor can send it back on the next write and be told it lost a race
///     rather than silently overwriting another tab's edit. No API key appears anywhere in this tree — only
///     <see cref="ExternalProviderConnectionResponse.HasApiKey" />.
/// </remarks>
public sealed record ExternalProviderConnectionsResponse
{
    /// <summary>An empty configuration: the shipped default, and what a node with no connections answers with.</summary>
    public static ExternalProviderConnectionsResponse Empty { get; } = new()
    {
        Revision = string.Empty
    };

    /// <summary>
    ///     The opaque revision of the stored file as it now stands. Round-tripped on a save as
    ///     <see cref="SaveExternalProviderConnectionRequest.ExpectedRevision" />.
    /// </summary>
    public required string Revision { get; init; }

    /// <summary>The configured connections, in stored order.</summary>
    public IReadOnlyList<ExternalProviderConnectionResponse> Connections { get; init; } = [];
}

/// <summary>One configured connection, without its API key.</summary>
public sealed record ExternalProviderConnectionResponse
{
    /// <summary>The immutable connection slug — the first segment of every <c>ext:</c> model id it owns.</summary>
    public required string Id { get; init; }

    /// <summary>The operator's label, shown in the picker, egress cues, and usage attribution.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The stored, already-normalized <c>…/v1/</c> base address.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>The declared trust locality as the <see cref="ExternalProviderLocality" /> enum name (<c>Local</c> | <c>Cloud</c>).</summary>
    public required string Locality { get; init; }

    /// <summary>The per-connection cloud grants; all false unless <see cref="Locality" /> is <c>Cloud</c>.</summary>
    public required ExternalProviderCloudGrantsResponse CloudGrants { get; init; }

    /// <summary>
    ///     True when an API key is stored for this connection. The key itself is NEVER returned; the editor renders a
    ///     placeholder from this flag and sends nothing back unless the operator types a new key.
    /// </summary>
    public required bool HasApiKey { get; init; }

    /// <summary>The per-connection network timeout in seconds, or null for the transport default.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>The operator's stored opt-in to a plain-http address that is not loopback.</summary>
    public required bool AllowInsecureHttp { get; init; }

    /// <summary>
    ///     Server-computed: the stored address is plain http and not loopback. With <see cref="AllowInsecureHttp" /> false
    ///     this is a connection saved before HTTPS became the default; it still works, and its next save needs the opt-in.
    /// </summary>
    public required bool InsecureTransport { get; init; }

    /// <summary>The models registered on this connection.</summary>
    public IReadOnlyList<ExternalProviderModelResponse> Models { get; init; } = [];

    /// <summary>The custom request headers. A secret header's value is never returned.</summary>
    public IReadOnlyList<ExternalProviderHeaderResponse> Headers { get; init; } = [];
}

/// <summary>A connection's grants of the five cloud permissions, each standing in for its node-wide <c>AllowCloudModel*</c> switch.</summary>
public sealed record ExternalProviderCloudGrantsResponse
{
    /// <summary>Node-local data: knowledge, attachments and file tools.</summary>
    public required bool LocalData { get; init; }

    /// <summary>Scheduled, graph and integration runs.</summary>
    public required bool UnattendedRuns { get; init; }

    /// <summary>The web tools.</summary>
    public required bool WebTools { get; init; }

    /// <summary>MCP tools.</summary>
    public required bool McpTools { get; init; }

    /// <summary>Sub-agent spawning.</summary>
    public required bool SubAgents { get; init; }
}

/// <summary>The cloud grants on a save. An absent member is false; the server ignores grants on a Local connection.</summary>
public sealed record ExternalProviderCloudGrantsRequest
{
    /// <summary>Node-local data: knowledge, attachments and file tools.</summary>
    public bool LocalData { get; init; }

    /// <summary>Scheduled, graph and integration runs.</summary>
    public bool UnattendedRuns { get; init; }

    /// <summary>The web tools.</summary>
    public bool WebTools { get; init; }

    /// <summary>MCP tools.</summary>
    public bool McpTools { get; init; }

    /// <summary>Sub-agent spawning.</summary>
    public bool SubAgents { get; init; }
}

/// <summary>One stored custom request header as the editor reads it.</summary>
public sealed record ExternalProviderHeaderResponse
{
    /// <summary>The header name.</summary>
    public required string Name { get; init; }

    /// <summary>The value of a plain header; always null for a secret header.</summary>
    public string? Value { get; init; }

    /// <summary>True when the value is write-only.</summary>
    public required bool IsSecret { get; init; }

    /// <summary>True when a value is stored. Lets the editor show a "stored" hint for a secret header it cannot read.</summary>
    public required bool HasStoredValue { get; init; }
}

/// <summary>One custom request header row on a save or a probe.</summary>
public sealed record ExternalProviderHeaderRequest
{
    /// <summary>The header name: an RFC 7230 token outside the reserved set.</summary>
    public string? Name { get; init; }

    /// <summary>The value. Blank on a secret row means "keep the stored value", on the stored origin only.</summary>
    public string? Value { get; init; }

    /// <summary>True when the value is write-only.</summary>
    public bool IsSecret { get; init; }
}

/// <summary>One registered model, with the namespaced id the rest of the node addresses it by.</summary>
public sealed record ExternalProviderModelResponse
{
    /// <summary>The backing model id sent on the wire verbatim.</summary>
    public required string WireId { get; init; }

    /// <summary>
    ///     The canonical <c>ext:{connectionId}/{wireId}</c> identity. Supplied by the server rather than composed in the
    ///     client, so the id the picker selects and the id the provider map routes are built by the same code.
    /// </summary>
    public required string ModelId { get; init; }

    /// <summary>Optional friendly label; consumers fall back to <see cref="WireId" />.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The declared context window in tokens, or null when the operator declared none.</summary>
    public int? ContextLength { get; init; }

    /// <summary>Whether the model may be offered tools.</summary>
    public required bool? SupportsTools { get; init; }

    /// <summary>Whether the model accepts image input.</summary>
    public required bool? SupportsVision { get; init; }

    /// <summary>Whether the model produces a reasoning channel.</summary>
    public required bool? SupportsReasoning { get; init; }

    /// <summary>Whether the endpoint honours a top-level <c>reasoning_effort</c> body field.</summary>
    public required bool? SupportsReasoningEffort { get; init; }

    /// <summary>The effort applied when the turn selects none, in the canonical lowercase vocabulary.</summary>
    public string? DefaultReasoningEffort { get; init; }
}

/// <summary>Request DTO for reading or deleting one connection. <see cref="ConnectionId" /> is bound from the route.</summary>
public sealed class GetExternalProviderConnectionRequest
{
    public string? ConnectionId { get; init; }
}

/// <summary>
///     Request DTO for deleting one connection. <see cref="ConnectionId" /> is bound from the route;
///     <see cref="ExpectedRevision" /> from the query string, because a DELETE carries no body.
/// </summary>
public sealed class DeleteExternalProviderConnectionRequest
{
    public string? ConnectionId { get; init; }

    /// <summary>
    ///     The revision the caller read, or null to delete unconditionally.
    /// </summary>
    /// <remarks>
    ///     Bound explicitly from the query string: a DELETE with a request body is a shape not every HTTP stack will
    ///     send, and without the attribute the generated OpenAPI document declares one.
    /// </remarks>
    [QueryParam]
    public string? ExpectedRevision { get; init; }
}

/// <summary>
///     Insert-or-replace one connection. <see cref="ConnectionId" /> is bound from the route, so the resource's
///     identity is the URL and can never disagree with the body.
/// </summary>
public sealed record SaveExternalProviderConnectionRequest
{
    /// <summary>The connection slug, from the route. Canonicalized and grammar-checked by the store.</summary>
    public string? ConnectionId { get; init; }

    /// <summary>The operator's label.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The operator-entered endpoint. Normalized to its canonical <c>…/v1/</c> form once, at save.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>The declared trust locality as a case-insensitive <see cref="ExternalProviderLocality" /> enum name.</summary>
    public string? Locality { get; init; }

    /// <summary>The cloud grants. Absent grants none; ignored unless <see cref="Locality" /> is <c>Cloud</c>.</summary>
    public ExternalProviderCloudGrantsRequest? CloudGrants { get; init; }

    /// <summary>
    ///     A NEW API key. ABSENT or blank means "keep whatever is stored" — the masked editor sends no key back, so
    ///     treating blank as "clear" would de-authenticate a working connection the first time it was renamed. Use
    ///     <see cref="ClearApiKey" /> to go back to keyless.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>Explicitly removes the stored key. Takes precedence over <see cref="ApiKey" />.</summary>
    public bool ClearApiKey { get; init; }

    /// <summary>The per-connection network timeout in seconds, or null for the transport default.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Opts a plain-http address that is not loopback in; without it such an address is refused with a 400.</summary>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>The models to register. May be empty: a probe-then-pick flow saves the connection first.</summary>
    public IReadOnlyList<SaveExternalProviderModelRequest> Models { get; init; } = [];

    /// <summary>The custom request headers. Absent means none; the list replaces the stored set.</summary>
    public IReadOnlyList<ExternalProviderHeaderRequest> Headers { get; init; } = [];

    /// <summary>The revision the caller read, or null to write unconditionally.</summary>
    public string? ExpectedRevision { get; init; }
}

/// <summary>One model registration on a save.</summary>
public sealed record SaveExternalProviderModelRequest
{
    /// <summary>The backing model id on the remote server.</summary>
    public string? WireId { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>The declared context window in tokens.</summary>
    public int? ContextLength { get; init; }

    public bool? SupportsTools { get; init; }

    public bool? SupportsVision { get; init; }

    public bool? SupportsReasoning { get; init; }

    public bool? SupportsReasoningEffort { get; init; }

    /// <summary>The effort applied when the turn selects none; validated against the chat reasoning vocabulary on save.</summary>
    public string? DefaultReasoningEffort { get; init; }
}

/// <summary>
///     A connect-time probe of either a stored connection or an unsaved draft.
/// </summary>
/// <remarks>
///     At least one of <see cref="ConnectionId" /> and <see cref="BaseUrl" /> must be present. Supplying both probes
///     the draft address while still falling back to the stored key — the shape the editor sends while an operator is
///     retyping the address of a connection whose (masked) key they have not re-entered.
/// </remarks>
public sealed record ExternalProviderProbeRequest
{
    /// <summary>A stored connection to take the address and, absent <see cref="ApiKey" />, the key from.</summary>
    public string? ConnectionId { get; init; }

    /// <summary>A raw, unsaved endpoint. Takes precedence over the stored connection's address.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>An explicit key. Blank means "use the stored key, if any". Never echoed back in the response.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Opts a plain-http address that is not loopback in; without it such an address is refused before any request.</summary>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>
    ///     The editor's draft header rows, sent with the probe. Absent means "use the stored connection's headers", an empty
    ///     list sends none; stored values travel only on the stored origin.
    /// </summary>
    public IReadOnlyList<ExternalProviderHeaderRequest>? Headers { get; init; }
}

/// <summary>What the probe found. Never carries the API key or a raw response body.</summary>
public sealed record ExternalProviderProbeResponse
{
    /// <summary>
    ///     True when the endpoint ANSWERED — including with a 404 or a 401. A connection whose server implements only
    ///     <c>POST /v1/chat/completions</c> is fully usable, so a missing model listing is reported through
    ///     <see cref="Error" /> and an empty <see cref="Models" /> rather than as unreachable.
    /// </summary>
    public required bool Reachable { get; init; }

    /// <summary>An operator-safe explanation, or null when the listing came back cleanly.</summary>
    public string? Error { get; init; }

    /// <summary>The model ids the endpoint listed, for pick-to-add. Empty when it serves no usable listing.</summary>
    public IReadOnlyList<ExternalProviderProbeModelResponse> Models { get; init; } = [];
}

/// <summary>One probed model id, with the context window when the server volunteered one.</summary>
public sealed record ExternalProviderProbeModelResponse
{
    /// <summary>The backing model id exactly as the server spells it.</summary>
    public required string Id { get; init; }

    /// <summary>The declared window from <c>max_model_len</c> or <c>context_length</c>, or null when neither was present.</summary>
    public int? ContextLength { get; init; }
}
