namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     The risk class an agent tool falls into, which the node-default tool-approval policy reads to decide whether a
///     whole category should require an approval round-trip before executing.
/// </summary>
/// <remarks>
///     Declared at each tool's own definition site, mirroring <c>RequiresApproval</c>, and carried on the offer
///     descriptors into <c>AllowedToolDto.Category</c>. It groups tools by what a call can DO: a stdio MCP tool is
///     <see cref="WriteExecute" />, an HTTP one <see cref="Network" />, and the four work-session state tools carry
///     <see cref="WriteExecute" /> — which is what <c>GRAPH-C4-2</c>'s runtime half judges an Agent node on. See
///     docs/wiki/04-agent-mode.md ("The risk taxonomy (`ToolCategory`)").
/// </remarks>
public enum ToolCategory
{
    /// <summary>Read-only, node-local, side-effect-free tools (fail-closed order: keep the non-default values distinct).</summary>
    ReadLocal,

    /// <summary>Tools that can write files or run commands on the node.</summary>
    WriteExecute,

    /// <summary>Tools that can spawn or drive other agents or models (<c>spawn_subagent</c>).</summary>
    Orchestration,

    /// <summary>Tools that reach an external or out-of-process surface (MCP tools).</summary>
    Network,

    /// <summary>
    ///     The fail-closed default for a tool that has not declared a category. The node policy treats an
    ///     <see cref="Unknown" /> tool as requiring approval so a new, uncategorized tool never silently auto-executes.
    /// </summary>
    Unknown
}
