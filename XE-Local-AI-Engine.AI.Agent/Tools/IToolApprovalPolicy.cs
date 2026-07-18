namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Node-level TIGHTEN-ONLY approval policy for agent tools (OPP-03). Consulted by the agent-definition resolver when
///     projecting a bound agent's allowed tools: for each offered tool it returns whether the tool must be gated behind an
///     approval round-trip before it executes. The policy may only ADD approval, never waive it — it composes ON TOP of
///     the tool's catalog default and can only turn a non-approval tool into an approval-requiring one, never the reverse.
///     <para>
///         This is deliberately NOT the structural floor. High-risk tools are already wrapped in
///         <c>ApprovalRequiredAIFunction</c> at their registries (MCP tools in the connection manager,
///         <c>run_in_agent_home</c> in the ClientLocal registry); that pre-wrap remains the last line of defense
///         regardless of what this policy returns. The policy adds a node-configurable, category- and name-scoped layer
///         above that floor so an operator can require approval for a whole risk class (e.g. every
///         <see cref="ToolCategory.Network" /> tool) without editing individual agents.
///     </para>
/// </summary>
public interface IToolApprovalPolicy
{
    /// <summary>
    ///     Returns whether <paramref name="toolName" /> (of risk class <paramref name="category" />) must require approval,
    ///     given its <paramref name="catalogDefault" /> approval flag. TIGHTEN-ONLY: an implementation must return
    ///     <see langword="true" /> whenever <paramref name="catalogDefault" /> is <see langword="true" /> (it can never
    ///     waive a default-on approval) and may additionally return <see langword="true" /> for a default-off tool its
    ///     node configuration tightens. It must never return <see langword="false" /> when <paramref name="catalogDefault" />
    ///     is <see langword="true" />.
    /// </summary>
    /// <param name="toolName">The offered tool's name (for per-tool-name overrides).</param>
    /// <param name="category">The tool's risk class; <see cref="ToolCategory.Unknown" /> is treated as fail-closed.</param>
    /// <param name="catalogDefault">The tool's own catalog approval flag (the floor the policy composes on top of).</param>
    bool RequiresApproval(string toolName, ToolCategory category, bool catalogDefault);
}

/// <summary>
///     No-op <see cref="IToolApprovalPolicy" /> floor: returns <paramref name="catalogDefault" /> unchanged (identity), so
///     a host that has not configured a node-level policy behaves byte-for-byte as it did before OPP-03. Wired via
///     <c>TryAddSingleton</c> so a provider-only host (or a test) always resolves a policy; the real, node-configured
///     <c>NodeToolApprovalPolicy</c> (registered by the composition root via a plain <c>AddSingleton</c>) wins over this
///     floor (last registration wins). Mirrors <c>NoOpGpuModelLoadAdmission</c>.
/// </summary>
public sealed class PermissiveToolApprovalPolicy : IToolApprovalPolicy
{
    /// <inheritdoc />
    public bool RequiresApproval(string toolName, ToolCategory category, bool catalogDefault)
    {
        // Identity: no node-level tightening. The tool's own catalog default is authoritative.
        return catalogDefault;
    }
}
