namespace XE_Local_AI_Engine.Client.Services.AgentHome;

using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>The <c>allowedActions</c> values the AgentHome tool schema offers, as the one place the node spells them.</summary>
/// <remarks>
///     Every value here gates something real. <c>propose_memory</c> used to be a fifth value; it was removed rather
///     than left advertised, because the node collected the sandbox's proposals and then discarded them.
/// </remarks>
internal static class AgentHomeAllowedActions
{
    /// <summary>Grants the goal loop the read tools (<c>list_files</c> / <c>read_file</c> / <c>search_text</c>).</summary>
    public const string ReadWorkspace = "read_workspace";

    /// <summary>Grants the goal loop <c>write_file</c>.</summary>
    public const string WriteWorkspace = "write_workspace";

    /// <summary>Grants the goal loop <c>run_command</c>.</summary>
    public const string RunCommands = "run_commands";

    /// <summary>Grants the post-run patch export.</summary>
    public const string ExportPatch = "export_patch";
}

/// <summary>
///     Runs a model-supplied <c>goal</c> as a bounded agent loop inside an already-prepared AgentHome sandbox.
/// </summary>
/// <remarks>
///     This is the seam that turns <c>run_in_agent_home</c>'s goal into work, and it stays out of
///     <see cref="IAgentHomeService" />: the lifecycle service owns identity, the lease, the workspace copy and the
///     patch export and has no model dependency, while this owns the inner agent and the sandbox-scoped tools.
/// </remarks>
internal interface IAgentHomeGoalExecutor
{
    /// <summary>
    ///     Executes <see cref="AgentHomeGoalRequest.Goal" /> against the prepared sandbox and reports what actually
    ///     happened. Never throws for a refused or budget-capped run — those are outcomes the model must be told about.
    ///     A caller cancel propagates as <see cref="OperationCanceledException" />.
    /// </summary>
    Task<AgentHomeGoalOutcome> ExecuteAsync(AgentHomeGoalRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Inputs for one goal-execution loop inside a prepared sandbox.</summary>
internal sealed record AgentHomeGoalRequest
{
    /// <summary>The live sandbox the loop's tools operate inside.</summary>
    public required SandboxHandle Handle { get; init; }

    /// <summary>The run id; used as the prefix of each command's sandbox execution id.</summary>
    public required string RunId { get; init; }

    /// <summary>The model-supplied goal, verbatim.</summary>
    public required string Goal { get; init; }

    /// <summary>The validated <c>allowedActions</c>; each value decides whether the matching inner tool exists at all.</summary>
    public required IReadOnlyList<string> AllowedActions { get; init; }

    /// <summary>The workspace aliases the run copied, so the inner prompt can name the folders the model may work in.</summary>
    public required IReadOnlyList<string> WorkspaceAliases { get; init; }

    /// <summary>The run's logger; commands are appended to <c>commands.jsonl</c> as they complete.</summary>
    public required IAgentHomeRunLogger RunLogger { get; init; }
}

/// <summary>Why a goal-execution loop stopped.</summary>
internal enum AgentHomeGoalStatus
{
    /// <summary>The loop never ran; <see cref="AgentHomeGoalOutcome.NotRunReason" /> says why.</summary>
    NotRun,

    /// <summary>The inner agent finished on its own.</summary>
    Completed,

    /// <summary>The inner agent asked for more tool calls than <see cref="AgentHomeOptions.MaxInnerToolCalls" /> allows.</summary>
    ToolCallBudgetExceeded,

    /// <summary>The loop ran past <see cref="AgentHomeOptions.MaxRunSeconds" /> and was cut off.</summary>
    TimeBudgetExceeded,

    /// <summary>The inner run raised; partial work (and the patch of it) still stands.</summary>
    Failed
}

/// <summary>One command the goal loop ran, as the model-facing result reports it.</summary>
internal sealed class AgentHomeCommandOutcome
{
    public required string Executable { get; init; }

    public required int ExitCode { get; init; }

    public required bool Completed { get; init; }
}

/// <summary>What a goal-execution loop actually did. Every field is model-safe: no host path, no captured output.</summary>
internal sealed record AgentHomeGoalOutcome
{
    /// <summary>How the loop ended.</summary>
    public required AgentHomeGoalStatus Status { get; init; }

    /// <summary>Why the loop never started, when <see cref="Status" /> is <see cref="AgentHomeGoalStatus.NotRun" />.</summary>
    public string? NotRunReason { get; init; }

    /// <summary>
    ///     Wall clock the loop ran for, from the same <c>GetUtcNow()</c> reading the
    ///     <see cref="AgentHomeOptions.MaxRunSeconds" /> deadline is computed from, so the two cannot disagree.
    ///     <see cref="TimeSpan.Zero" /> when the loop never started.
    /// </summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>How many inner tool calls were made (refused ones included — they cost a turn).</summary>
    public int ToolCallCount { get; init; }

    /// <summary>How many inner tool calls were refused by a guard or a budget.</summary>
    public int RefusedCallCount { get; init; }

    /// <summary>The workspace-relative paths <c>write_file</c> wrote, in order, de-duplicated.</summary>
    public IReadOnlyList<string> WrittenFiles { get; init; } = [];

    /// <summary>The commands <c>run_command</c> ran, in order.</summary>
    public IReadOnlyList<AgentHomeCommandOutcome> Commands { get; init; } = [];

    /// <summary>The inner tool names the loop was handed, for the run log and the tests that pin the set.</summary>
    public IReadOnlyList<string> OfferedToolNames { get; init; } = [];

    /// <summary>
    ///     Why <c>run_command</c> was withheld although <c>run_commands</c> was granted, or <see langword="null" />
    ///     when it was not withheld.
    /// </summary>
    /// <remarks>
    ///     Set when this node cannot give the sandbox a real filesystem boundary: a model-chosen command without one
    ///     reads and writes any path the engine's user can, so the action is refused rather than served weaker. The
    ///     model is TOLD, because a silently missing granted tool is what a model reports as "I did the work" having
    ///     done none of it.
    /// </remarks>
    public string? CommandsUnavailableReason { get; init; }

    /// <summary>Whether the loop ran at all — the one thing the result may never overstate.</summary>
    public bool Executed => Status != AgentHomeGoalStatus.NotRun;
}
