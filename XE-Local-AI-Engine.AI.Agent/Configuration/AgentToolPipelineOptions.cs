namespace XE_Local_AI_Engine.AI.Agent.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Shared bounds for the agent's function-invocation (tool) pipeline.
/// </summary>
/// <remarks>
///     Every setting pins a behavior that would otherwise ride an implicit framework default or run unbounded, so a
///     framework upgrade or a looping local model cannot silently change the ceiling.
/// </remarks>
public sealed class AgentToolPipelineOptions
{
    public const string Section = "Agent:ToolPipeline";

    /// <summary>Default for <see cref="MaxToolCallsPerResponse" />.</summary>
    public const int DefaultMaxToolCallsPerResponse = 32;

    /// <summary>
    ///     Hard cap on the tool round-trips the function-invocation pipeline runs for a single request, applied via
    ///     <c>FunctionInvokingChatClient.MaximumIterationsPerRequest</c>.
    /// </summary>
    /// <remarks>
    ///     Pinned explicitly — default 40, matching the Microsoft.Extensions.AI implicit default — so a framework
    ///     upgrade cannot change it.
    /// </remarks>
    [Range(1, 1000)]
    public int MaximumToolIterationsPerRequest { get; set; } = 40;

    /// <summary>
    ///     Shared backstop character budget for a single tool result before it enters — and is re-sent on every later
    ///     turn of — the chat history.
    /// </summary>
    /// <remarks>
    ///     Applied at the tool-result boundary to every ClientLocal and MCP tool; smaller per-tool caps still run first
    ///     inside each handler. Default 65536, deliberately above the largest per-tool budget (the 50K knowledge-base
    ///     and document handlers), so a handler's own score-ordered truncation and truncated-flags are never overridden
    ///     by a second blunt cut here. The backstop only catches tools with no cap of their own.
    /// </remarks>
    [Range(1024, int.MaxValue)]
    public int MaxToolResultCharacters { get; set; } = 65_536;

    /// <summary>
    ///     Per-tool, per-request ceiling on consecutive invalid-argument tool calls before the tool is disabled for the
    ///     rest of that request. Default 3.
    /// </summary>
    /// <remarks>
    ///     Arguments that fail schema validation, or that the handler cannot parse, return a model-actionable repair
    ///     result instead of throwing; after this many consecutive repairs for the same tool in one request it returns
    ///     a terminal "disabled for this run" result, so a small model cannot burn the whole iteration budget looping
    ///     on the same malformed call. A single valid call resets the counter.
    /// </remarks>
    [Range(1, 100)]
    public int MaxConsecutiveInvalidToolCallsPerTool { get; set; } = 3;

    /// <summary>
    ///     Ceiling on the tool calls ONE model response may hand the function-invocation loop. Default 32.
    /// </summary>
    /// <remarks>
    ///     <see cref="MaximumToolIterationsPerRequest" /> counts round-trips, so a single runaway response (a small model
    ///     once emitted 1,584 identical calls in one turn) is otherwise unbounded. Identical calls (name + arguments)
    ///     collapse to the first; past the ceiling the rest are dropped before the loop sees them.
    /// </remarks>
    [Range(1, 1000)]
    public int MaxToolCallsPerResponse { get; set; } = DefaultMaxToolCallsPerResponse;
}
