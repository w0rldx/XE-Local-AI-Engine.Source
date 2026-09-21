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

/// <summary>
///     No-op <see cref="IToolApprovalPolicy" /> floor that applies no node-level tightening, so a host without a
///     node-configured policy behaves byte-for-byte as it did before this layer existed. Mirrors
///     <c>NoOpGpuModelLoadAdmission</c>.
/// </summary>
/// <remarks>
///     Wired via <c>TryAddSingleton</c> so a provider-only host or a test always resolves a policy; the real
///     <c>NodeToolApprovalPolicy</c>, registered by the composition root with a plain <c>AddSingleton</c>, wins under
///     last-registration-wins. The one non-identity term is the fail-closed <see cref="ToolCategory.Unknown" /> rule
///     both policies share — an uncategorized tool is never auto-executed — kept here for defense in depth, and
///     byte-identical today because no offered tool ships <see cref="ToolCategory.Unknown" />.
/// </remarks>
public sealed class PermissiveToolApprovalPolicy : IToolApprovalPolicy
{
    /// <inheritdoc />
    public bool RequiresApproval(string toolName, ToolCategory category, bool catalogDefault)
    {
        // Identity on the catalog default, with the shared fail-closed Unknown rule: an uncategorized tool always
        // requires approval. No offered tool ships Unknown today, so this is byte-identical to the pre-policy behavior.
        return catalogDefault || category == ToolCategory.Unknown;
    }
}
