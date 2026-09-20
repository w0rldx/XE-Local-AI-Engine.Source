namespace XE_Local_AI_Engine.Client.Services.Mcp;

using System.Runtime.Serialization;
using System.Text.Json.Serialization;

/// <summary>
///     Owns the lifecycle of the single bearer credential authenticating an EXTERNAL MCP client against this node's
///     inbound MCP server endpoint: generation, retrieval for display, revocation and constant-time comparison.
/// </summary>
/// <remarks>
///     This is the INBOUND direction. <see cref="IMcpServerService" /> and <c>IMcpServerConnectionManager</c> own the
///     OUTBOUND direction, this node connecting to third-party MCP servers, and share nothing with it.
/// </remarks>
public interface IMcpServerApiKeyService
{
    /// <summary>
    ///     Mints a fresh key, REPLACING any existing one, and returns it in full.
    /// </summary>
    /// <remarks>
    ///     This is the ONLY time the plaintext key exists outside the caller that presents it: only its SHA-256 digest
    ///     is persisted, so a key not captured from this return value is gone and can only be replaced by generating
    ///     another. Every other surface, <see cref="GetAsync" /> included, sees only the prefix.
    /// </remarks>
    Task<GeneratedMcpServerApiKey> GenerateAsync(CancellationToken cancellationToken = default) =>
        GenerateAsync(McpServerApiKeyScope.Delegate, cancellationToken);

    /// <summary>Mints a replacement key for the requested caller scope.</summary>
    Task<GeneratedMcpServerApiKey> GenerateAsync(McpServerApiKeyScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the current credential's non-secret metadata, or <see langword="null" /> when none has been
    ///     generated.
    /// </summary>
    /// <remarks>
    ///     It deliberately cannot return the key: the node stores only a one-way digest, so a lost key is
    ///     unrecoverable and the operator must generate a replacement and reconfigure every client.
    /// </remarks>
    Task<McpServerApiKeyView?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Revokes the credential. Returns <see langword="true" /> when one existed. The MCP endpoint then authenticates nobody.</summary>
    Task<bool> RevokeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns trusted scope metadata when <paramref name="presented" /> matches the stored key, otherwise null.
    ///     Compares in constant time and stamps last-used on success. A node with no key never authenticates.
    /// </summary>
    Task<McpServerApiKeyValidation?> ValidateAsync(string? presented, CancellationToken cancellationToken = default);
}

/// <summary>The trust level carried by the singleton inbound-MCP credential.</summary>
public enum McpServerApiKeyScope
{
    [EnumMember(Value = "delegate")] [JsonStringEnumMemberName("delegate")]
    Delegate = 0,

    [EnumMember(Value = "agentic")] [JsonStringEnumMemberName("agentic")]
    Agentic = 1
}

/// <summary>
///     The credential as shown to the operator. Carries no secret by construction — the key is not recoverable from
///     the node — so this shape is safe to return from any Operator-gated surface.
/// </summary>
public sealed class McpServerApiKeyView
{
    public required string Prefix { get; init; }

    public required McpServerApiKeyScope Scope { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset? LastUsedAt { get; init; }
}

/// <summary>Trusted metadata produced by a successful, single-lookup key validation.</summary>
public sealed class McpServerApiKeyValidation
{
    public required McpServerApiKeyScope Scope { get; init; }

    public required string Prefix { get; init; }
}

/// <summary>
///     A freshly minted credential: the one-time plaintext <see cref="Key" /> plus the metadata that stays
///     retrievable afterwards.
/// </summary>
/// <remarks>
///     It is separate from <see cref="McpServerApiKeyView" /> so the type system, not a comment, is what stops the
///     secret being returned from a retrieval path. Never log it, never persist it, never put it in an audit record or
///     an error body: once this value is dropped, the key is gone.
/// </remarks>
public sealed class GeneratedMcpServerApiKey
{
    public required string Key { get; init; }

    public required McpServerApiKeyView View { get; init; }
}
