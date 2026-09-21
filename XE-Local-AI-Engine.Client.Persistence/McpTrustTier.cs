namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     How much of this node an outbound MCP server is trusted with.
/// </summary>
/// <remarks>
///     Operator decision D-C; the rationale, including why there is no <c>Remote</c> tier, is in
///     <c>docs/security/mcp-trust-tiers.md</c>. The tier decides WHERE a stdio server's process runs, the only
///     control that actually bounds what a third-party executable can reach. It is not an approval knob: every MCP
///     tool is already approval-required, pre-wrapped in <c>ApprovalRequiredAIFunction</c> and ineligible for a
///     remembered session approval. What the tier changes there is the tool's <c>ToolCategory</c>.
/// </remarks>
public enum McpTrustTier
{
    /// <summary>
    ///     The default for every stdio registration: the server runs inside the substrate under
    ///     <c>SandboxWorkloads.McpStdio</c> — no host filesystem, no network, a disposable jail as its directory.
    /// </summary>
    /// <remarks>
    ///     A host that cannot serve that boundary refuses the connection rather than degrading to a host launch — see
    ///     <c>SandboxedMcpStdioTransport</c>.
    /// </remarks>
    Sandboxed = 0,

    /// <summary>
    ///     A plain host child with access to the operator's filesystem and network, and only the environment scrubbed.
    /// </summary>
    /// <remarks>
    ///     Reachable only by an explicit per-server operator opt-in, never as a fallback and never inferred from the
    ///     command. Its tools are <c>ToolCategory.WriteExecute</c>.
    /// </remarks>
    PrivilegedHost = 1,

    /// <summary>
    ///     Reserved for a transport the engine itself owns. Nothing sets it, and the CRUD surface rejects it.
    /// </summary>
    /// <remarks>
    ///     It exists so that "engine-owned" is a value in the vocabulary rather than an absence a future consumer
    ///     would express by picking one of the other two.
    /// </remarks>
    BuiltInTrusted = 2
}
