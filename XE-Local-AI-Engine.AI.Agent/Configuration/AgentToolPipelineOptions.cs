namespace XE_Local_AI_Engine.AI.Agent.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Shared bounds for the agent's function-invocation (tool) pipeline. Both settings pin a behavior that would
///     otherwise ride an implicit framework default or run unbounded, so a framework upgrade or a looping local model
///     cannot silently change the ceiling.
/// </summary>
public sealed class AgentToolPipelineOptions
{
    /// <summary>Configuration section holding the tool-pipeline bounds.</summary>
    public const string Section = "Agent:ToolPipeline";

    /// <summary>
    ///     Hard cap on the number of tool round-trips the function-invocation pipeline runs for a single request,
    ///     applied via <c>FunctionInvokingChatClient.MaximumIterationsPerRequest</c>. Pinned explicitly (default 40,
    ///     matching the current Microsoft.Extensions.AI implicit default) so a framework upgrade cannot change it.
    /// </summary>
    [Range(1, 1000)]
    public int MaximumToolIterationsPerRequest { get; set; } = 40;

    /// <summary>
    ///     Shared backstop character budget for a single tool result before it enters (and is re-sent on every
    ///     subsequent turn of) the chat history. Applied at the tool-result boundary to every ClientLocal and MCP tool;
    ///     smaller per-tool caps still run first inside each handler. Default 65536 — deliberately above the largest
    ///     per-tool budget (the 50K knowledge-base/document handlers), so a handler's own score-ordered truncation and
    ///     truncated-flags are never overridden by a second blunt cut here; the backstop only catches tools with no
    ///     cap of their own.
    /// </summary>
    [Range(1024, int.MaxValue)]
    public int MaxToolResultCharacters { get; set; } = 65_536;
}
