namespace XE_Local_AI_Engine.AI.Agent.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Shared bounds for the agent's function-invocation (tool) pipeline. Both settings pin a behavior that would
///     otherwise ride an implicit framework default or run unbounded, so a framework upgrade or a looping local model
///     cannot silently change the ceiling.
/// </summary>
public sealed class AgentToolPipelineOptions
{
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

    /// <summary>
    ///     Per-tool, per-request ceiling on consecutive invalid-argument tool calls before the tool is disabled for the
    ///     rest of that request. Each time a model supplies arguments that fail schema validation (or the handler cannot
    ///     parse them), the tool returns a model-actionable repair result instead of throwing; once this many consecutive
    ///     repairs have been returned for the same tool in one request, the tool returns a terminal "disabled for this
    ///     run" result so a small model cannot burn the whole iteration budget looping on the same malformed call. A
    ///     single valid call resets the counter. Default 3.
    /// </summary>
    [Range(1, 100)]
    public int MaxConsecutiveInvalidToolCallsPerTool { get; set; } = 3;
}
