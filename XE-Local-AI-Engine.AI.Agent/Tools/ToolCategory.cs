namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     The risk class an agent tool falls into, used by the node-default tool-approval policy to decide whether
///     a whole category of tools should require an approval round-trip before executing. Declared at each tool's own
///     definition site (mirroring how <c>RequiresApproval</c> is declared) and carried on the offer descriptors into
///     <c>AllowedToolDto.Category</c>, where the policy layer reads it.
///     <para>
///         The taxonomy is deliberately coarse — it groups tools by what a call can DO, not by which tool it is:
///         <list type="bullet">
///             <item><see cref="ReadLocal" /> — read-only, node-local, side-effect-free reads (clock/arithmetic, the
///             read-only coder workspace tools, the read-only knowledge-base tools).</item>
///             <item><see cref="WriteExecute" /> — tools that can write files or run commands on the node. The four
///             work-session state tools carry it in the categorized offer, and a workflow Agent node is judged on
///             exactly that (<c>GRAPH-C4-2</c>'s runtime half, which excludes those four by name because every agent
///             node is offered them). A stdio MCP tool is <see cref="WriteExecute" /> too; an HTTP one is
///             <see cref="Network" />. The one write/execute gateway that is NOT here — <c>run_in_agent_home</c> — lives
///             on the ClientLocal registry seam rather than the offer, and is floored via the registry pre-wrap
///             (<c>ApprovalRequiredAIFunction</c>), not this category.</item>
///             <item><see cref="Orchestration" /> — can spawn or drive other agents/models (<c>spawn_subagent</c>).</item>
///             <item><see cref="Network" /> — reaches an external/out-of-process surface (every discovered MCP tool).</item>
///             <item><see cref="Unknown" /> — the fail-closed default for any tool that has not declared a category, so
///             an uncategorized tool is treated as approval-requiring by the node policy rather than silently
///             auto-executing.</item>
///         </list>
///     </para>
/// </summary>
public enum ToolCategory
{
    /// <summary>Read-only, node-local, side-effect-free tools (fail-closed order: keep the non-default values distinct).</summary>
    ReadLocal,

    /// <summary>Tools that can write files or run commands on the node.</summary>
    WriteExecute,

    /// <summary>Tools that can spawn or drive other agents or models.</summary>
    Orchestration,

    /// <summary>Tools that reach an external or out-of-process surface (MCP tools).</summary>
    Network,

    /// <summary>
    ///     The fail-closed default for a tool that has not declared a category. The node policy treats an
    ///     <see cref="Unknown" /> tool as requiring approval so a new, uncategorized tool never silently auto-executes.
    /// </summary>
    Unknown
}
