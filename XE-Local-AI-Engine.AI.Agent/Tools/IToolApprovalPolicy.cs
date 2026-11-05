namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Node-level TIGHTEN-ONLY approval policy for agent tools: for each offered tool it returns whether the tool must
///     be gated behind an approval round-trip before it executes.
/// </summary>
/// <remarks>
///     Consulted by the agent-definition resolver when projecting a bound agent's allowed tools. It composes ON TOP of
///     the catalog default and may only ADD approval, never waive it, and it is deliberately NOT the structural floor:
///     MCP tools and <c>run_in_agent_home</c> are already wrapped in <c>ApprovalRequiredAIFunction</c> at their
///     registries, and that pre-wrap remains the last line of defense whatever this returns. See
///     docs/wiki/04-agent-mode.md ("Effective approval policy").
/// </remarks>
public interface IToolApprovalPolicy
{
    /// <summary>
    ///     Returns whether <paramref name="toolName" />, of risk class <paramref name="category" />, must require
    ///     approval given its <paramref name="catalogDefault" /> approval flag.
    /// </summary>
    /// <remarks>
    ///     TIGHTEN-ONLY: an implementation must return <see langword="true" /> whenever
    ///     <paramref name="catalogDefault" /> is <see langword="true" />, so it can never waive a default-on approval,
    ///     and may additionally return <see langword="true" /> for a default-off tool its node configuration tightens.
    /// </remarks>
    /// <param name="toolName">The offered tool's name (for per-tool-name overrides).</param>
    /// <param name="category">The tool's risk class; <see cref="ToolCategory.Unknown" /> is treated as fail-closed.</param>
    /// <param name="catalogDefault">The tool's own catalog approval flag (the floor the policy composes on top of).</param>
    bool RequiresApproval(string toolName, ToolCategory category, bool catalogDefault);
}
