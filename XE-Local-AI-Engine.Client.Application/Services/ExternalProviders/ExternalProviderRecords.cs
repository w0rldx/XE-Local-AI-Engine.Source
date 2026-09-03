namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The whole persisted external-provider configuration: the schema version the payload was written at, an opaque
///     revision for compare-and-swap saves, and the operator's connections.
/// </summary>
/// <remarks>
///     Versioned from the first release rather than retrofitted, because this file holds API keys: a shape change that
///     cannot be recognized has to be distinguishable from a decryption failure, and only the former is safe to lift
///     rather than discard.
/// </remarks>
public sealed record StoredExternalProviderConfig
{
    /// <summary>The schema this payload was written at. Bumped only for a shape change the reader must branch on.</summary>
    public int SchemaVersion { get; init; } = ExternalProviderStoreSchema.CurrentVersion;

    /// <summary>
    ///     Opaque revision of the whole file, regenerated on every successful write. A caller that read the config,
    ///     built an edit from it, and saves with a stale revision is rejected rather than silently overwriting the
    ///     concurrent edit — the same compare-and-swap discipline the provider map uses for its rows.
    /// </summary>
    public string Revision { get; init; } = string.Empty;

    /// <summary>The configured connections, in the order the operator's edits produced.</summary>
    public IReadOnlyList<StoredExternalProviderConnection> Connections { get; init; } = [];
}

/// <summary>
///     One persisted connection. This is the ONLY type in the feature that carries the API key; everything downstream
///     consumes the key-free <see cref="ExternalProviderConnectionDescriptor" /> and fetches the key by itself when it
///     genuinely needs to authenticate.
/// </summary>
public sealed record StoredExternalProviderConnection
{
    /// <summary>The immutable canonical connection slug (<see cref="ExternalModelId.ConnectionIdPattern" />).</summary>
    public required string Id { get; init; }

    /// <summary>The operator's label for the connection.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    ///     The base address, ALREADY normalized by <c>OpenAICompatibleBaseAddress</c> at save time. Stored as a string
    ///     because a <see cref="Uri" /> round-trips through JSON less predictably than the canonical spelling the
    ///     normalizer produced; readers parse it back without re-normalizing.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>The decrypted API key, or <see langword="null" /> for a keyless connection. Never leaves this type.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The operator-declared trust locality driving every downstream gate.</summary>
    public required ExternalProviderLocality Locality { get; init; }

    /// <summary>Per-connection network timeout in seconds, or <see langword="null" /> for the transport default.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>The models the operator registered on this connection.</summary>
    public IReadOnlyList<StoredExternalProviderModel> Models { get; init; } = [];
}

/// <summary>
///     One persisted model registration. Deliberately FLAT rather than grouping the capability and reasoning flags into
///     nested objects: it maps one-to-one onto <see cref="ExternalProviderModelDescriptor" />, so the store's projection
///     onto the registry read model is a field copy with nowhere for a group to be half-populated.
/// </summary>
public sealed record StoredExternalProviderModel
{
    /// <summary>The backing model id sent on the wire verbatim.</summary>
    public required string WireId { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>The declared context window in tokens, or <see langword="null" /> when the operator declared none.</summary>
    public int? ContextLength { get; init; }

    /// <summary>Whether the model may be offered tools.</summary>
    public bool SupportsTools { get; init; }

    /// <summary>Whether the model accepts image input.</summary>
    public bool SupportsVision { get; init; }

    /// <summary>Whether the model produces a reasoning channel.</summary>
    public bool SupportsReasoning { get; init; }

    /// <summary>Whether the endpoint honours a top-level <c>reasoning_effort</c> body field.</summary>
    public bool SupportsReasoningEffort { get; init; }

    /// <summary>The effort applied when the turn selects none, in the canonical lowercase vocabulary.</summary>
    public string? DefaultReasoningEffort { get; init; }
}

/// <summary>Schema constants for the encrypted external-provider store.</summary>
public static class ExternalProviderStoreSchema
{
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Upper bound on configured connections — a guard against a hand-edited or corrupted payload, not a product limit.</summary>
    public const int MaxConnections = 32;

    /// <summary>Upper bound on registered models per connection.</summary>
    public const int MaxModelsPerConnection = 128;

    /// <summary>Upper bound on a connection's display name.</summary>
    public const int MaxDisplayNameLength = 100;

    /// <summary>Lower bound on a per-connection timeout, in seconds.</summary>
    public const int MinTimeoutSeconds = 5;

    /// <summary>
    ///     Upper bound on a per-connection timeout, in seconds. Generous by design: a self-hosted runtime paying a cold
    ///     model load legitimately takes minutes before its first token, so this is an outer floor against a wedged
    ///     socket, never a generation deadline.
    /// </summary>
    public const int MaxTimeoutSeconds = 3600;
}
