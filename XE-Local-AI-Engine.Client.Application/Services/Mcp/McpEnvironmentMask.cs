namespace XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     The placeholder a stdio MCP server's environment VALUES are replaced with on the way out of the node, and the
///     sentinel an update sends back to mean "keep what is stored".
/// </summary>
/// <remarks>
///     An MCP server's environment map is where its API keys live. It is AEAD-encrypted at rest
///     (<c>McpServerRegistration.EnvJson</c>, AAD column name <c>env</c>) and there is no editing reason to read one
///     back: the settings form needs the KEYS to render its rows, never the values. Masking is what makes that
///     encryption meaningful against anything holding a session. It lives in the application layer because both sides
///     of the round-trip need it — the mapper writes it, <c>McpServerService.UpdateAsync</c> reads it back.
/// </remarks>
public static class McpEnvironmentMask
{
    /// <summary>
    ///     Deliberately an explicit sentinel rather than a row of bullets, which an operator could plausibly type by
    ///     accident and silently mean "unchanged".
    /// </summary>
    /// <remarks>
    ///     A server whose environment genuinely holds this exact string keeps working: the value is preserved on
    ///     update instead of rewritten to itself.
    /// </remarks>
    public const string Value = "__XE_MCP_ENV_UNCHANGED__";
}
