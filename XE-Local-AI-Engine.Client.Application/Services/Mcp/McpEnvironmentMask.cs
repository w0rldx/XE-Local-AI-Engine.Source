namespace XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     The placeholder a stdio MCP server's environment VALUES are replaced with on the way out of the node, and the
///     sentinel an update sends back to mean "keep what is stored".
///     <para>
///         An MCP server's environment map is where its API keys live. It is AEAD-encrypted at rest
///         (<c>McpServerRegistration.EnvJson</c>, AAD column name <c>env</c>) and there is no editing reason to read
///         one back — the settings form needs the KEYS to render its rows, never the values. Masking is what makes the
///         encryption meaningful against anything holding a session rather than only against someone holding the file.
///     </para>
///     <para>
///         It lives in the application layer rather than on the endpoint DTO because both sides of the round-trip need
///         it: the mapper writes it, and <c>McpServerService.UpdateAsync</c> — the one place that has both the request
///         and the stored record — reads it back.
///     </para>
/// </summary>
public static class McpEnvironmentMask
{
    /// <summary>
    ///     Deliberately an explicit sentinel rather than a row of bullets: a value an operator could plausibly type by
    ///     accident would silently mean "unchanged". A server whose environment genuinely holds this exact string
    ///     keeps working — the value is simply preserved on update instead of rewritten to itself.
    /// </summary>
    public const string Value = "__XE_MCP_ENV_UNCHANGED__";
}
