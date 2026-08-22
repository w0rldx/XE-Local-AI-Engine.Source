namespace XE_Local_AI_Engine.Client.Services.Mcp;

using System.Runtime.Serialization;
using System.Text.Json.Serialization;

/// <summary>
///     Owns the lifecycle of the single bearer credential that authenticates an EXTERNAL MCP client against this node's
///     inbound MCP server endpoint: generation, retrieval for display, revocation, and the constant-time comparison the
///     authentication handler performs.
///     <para>
///         This is the INBOUND direction. <see cref="IMcpServerService" /> and <c>IMcpServerConnectionManager</c> own
///         the OUTBOUND direction (this node connecting to third-party MCP servers) and share nothing with it.
///     </para>
/// </summary>
public interface IMcpServerApiKeyService
{
    /// <summary>
    ///     Mints a fresh key, REPLACING any existing one, and returns it in full. This is the ONLY time the plaintext
    ///     key exists outside the caller that presents it: only its SHA-256 digest is persisted, so a key not captured
    ///     from this return value is gone and can only be replaced by generating another. Every other surface —
    ///     <see cref="GetAsync" /> included — sees only the prefix.
    /// </summary>
    Task<GeneratedMcpServerApiKey> GenerateAsync(CancellationToken cancellationToken = default) =>
        GenerateAsync(McpServerApiKeyScope.Delegate, cancellationToken);

    /// <summary>Mints a replacement key for the requested caller scope.</summary>
    Task<GeneratedMcpServerApiKey> GenerateAsync(McpServerApiKeyScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the current credential's non-secret metadata, or <see langword="null" /> when none has been
    ///     generated. Deliberately cannot return the key: the node stores only a one-way digest, so a lost key is
    ///     unrecoverable and the operator must generate a replacement and reconfigure every client.
    /// </summary>
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
    [EnumMember(Value = "delegate")]
    [JsonStringEnumMemberName("delegate")]
    Delegate = 0,

    [EnumMember(Value = "agentic")]
    [JsonStringEnumMemberName("agentic")]
    Agentic = 1
}

/// <summary>
///     The credential as shown to the operator. Carries no secret by construction — the key is not recoverable from
///     the node — so this shape is safe to return from any Operator-gated surface.
/// </summary>
public sealed record McpServerApiKeyView(string Prefix,
    McpServerApiKeyScope Scope,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt);

/// <summary>Trusted metadata produced by a successful, single-lookup key validation.</summary>
public sealed record McpServerApiKeyValidation(McpServerApiKeyScope Scope, string Prefix);

/// <summary>
///     A freshly minted credential: the one-time plaintext <see cref="Key" /> plus the metadata that will remain
///     retrievable afterwards. Separate from <see cref="McpServerApiKeyView" /> so the type system — not a comment —
///     is what stops the secret being returned from a retrieval path. Never log it, never persist it, never put it in
///     an audit record or an error body: once this value is dropped, the key is gone.
/// </summary>
public sealed record GeneratedMcpServerApiKey(string Key, McpServerApiKeyView View);
