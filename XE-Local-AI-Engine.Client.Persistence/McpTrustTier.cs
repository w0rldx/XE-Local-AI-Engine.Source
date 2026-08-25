namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     How much of this node an outbound MCP server is trusted with. Operator decision D-C (2026-08-25); the full
///     rationale, including why there is no <c>Remote</c> tier, is in <c>docs/security/mcp-trust-tiers.md</c>.
///     <para>
///         The tier decides WHERE a stdio server's process runs, which is the only control that actually bounds what a
///         third-party executable can reach. It is not an approval knob: every MCP tool is already approval-required,
///         already pre-wrapped in <c>ApprovalRequiredAIFunction</c>, and already ineligible for a remembered
///         session approval. What the tier changes on that side is the tool's <c>ToolCategory</c>, so the class an
///         operator sees is the truthful one.
///     </para>
/// </summary>
public enum McpTrustTier
{
    /// <summary>
    ///     The default for every stdio registration. The server runs inside the substrate under
    ///     <c>SandboxWorkloads.McpStdio</c>: no host filesystem, no network, a disposable jail as its working
    ///     directory. A host that cannot serve that boundary refuses the connection rather than degrading to a host
    ///     launch — see <c>SandboxedMcpStdioTransport</c>.
    /// </summary>
    Sandboxed = 0,

    /// <summary>
    ///     A plain host child, exactly as every stdio server ran before Phase 2 — the operator's filesystem and the
    ///     operator's network, with only the environment scrubbed. Reachable only by an explicit per-server operator
    ///     opt-in, never as a fallback and never inferred from the command. Its tools are
    ///     <c>ToolCategory.WriteExecute</c>.
    /// </summary>
    PrivilegedHost = 1,

    /// <summary>
    ///     Reserved for a transport the engine itself owns. Nothing sets it today, and the CRUD surface rejects it, so
    ///     it cannot be reached from the API or the UI. It exists so that "engine-owned" is a value in the vocabulary
    ///     rather than an absence a future consumer would express by picking one of the other two.
    /// </summary>
    BuiltInTrusted = 2
}
