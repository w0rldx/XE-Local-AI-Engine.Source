namespace XE_Local_AI_Engine.Client.Services.Mcp;

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
    ///     Mints a fresh key, REPLACING any existing one, and returns it in full. The plaintext key is returned here and
    ///     by <see cref="GetAsync" />; every other surface sees only the prefix.
    /// </summary>
    Task<McpServerApiKeyView> GenerateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the current credential including its plaintext material, or <see langword="null" /> when none has
    ///     been generated. Reversible retrieval is a deliberate product decision — see <c>McpServerApiKey</c>.
    /// </summary>
    Task<McpServerApiKeyView?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Revokes the credential. Returns <see langword="true" /> when one existed. The MCP endpoint then authenticates nobody.</summary>
    Task<bool> RevokeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="presented" /> matches the stored key. Compares in
    ///     constant time and stamps last-used on success. A node with no key never authenticates, so an absent key is a
    ///     closed door rather than an open one.
    /// </summary>
    Task<bool> ValidateAsync(string? presented, CancellationToken cancellationToken = default);
}

/// <summary>
///     The credential as shown to the operator. <see cref="Key" /> is the full secret — it belongs only in the node
///     settings response the operator explicitly requested, never in a log, an audit record or an error body.
/// </summary>
public sealed record McpServerApiKeyView(string Prefix, string Key, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);
