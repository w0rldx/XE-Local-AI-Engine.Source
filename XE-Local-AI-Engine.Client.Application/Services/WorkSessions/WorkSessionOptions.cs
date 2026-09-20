namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Configuration for agent work sessions.
/// </summary>
/// <remarks>
///     <c>appsettings.json</c> ships <c>WorkSessions:Enabled: true</c>, so the feature is on for every node that does not override the
///     key, while the compiled-in default of <see cref="Enabled" /> is <see langword="false" /> — what a host binding a configuration
///     source WITHOUT the key gets, which is most test hosts and nothing that ships. <see cref="Enabled" /> gates BEHAVIOUR, never
///     registration: the endpoints and the hub are mapped unconditionally, so a disabled node answers <c>404</c> from request-path
///     middleware ahead of authentication rather than the <c>500</c> an empty container would give.
/// </remarks>
public sealed class WorkSessionOptions
{
    public const string Section = "WorkSessions";

    public bool Enabled { get; init; }

    /// <summary>Steps one start or resume may take before the session parks with a checkpoint. Not a lifetime budget.</summary>
    [Range(1, 1000)]
    public int MaxStepsPerRun { get; init; } = 25;

    [Range(1, 1000)]
    public int CheckpointEveryNSteps { get; init; } = 5;

    /// <summary>
    ///     An admission cap, not a concurrency setting. The node has one invocation slot, so a second running session
    ///     buys nothing; the value exists so the cap is configurable rather than compiled in.
    /// </summary>
    [Range(1, 64)]
    public int MaxConcurrentSessions { get; init; } = 1;

    /// <summary>
    ///     How long a session may sit waiting on an approval or a question before it is demoted to <c>Paused</c>: an
    ///     unattended parked session would otherwise hold the node's only invocation slot indefinitely.
    /// </summary>
    /// <remarks>
    ///     It must stay strictly under <c>WorkerNode:MaxPendingToolCallAgeMinutes</c>, or the node expires the pending tool call the
    ///     session is parked on before the park clock fires and the park times out against a prompt nobody can answer. The range's
    ///     upper bound is 3599 because that node setting itself caps at 60 minutes. <c>WorkSessionOptionsValidator</c> checks the
    ///     relation at startup against the CONFIGURED seed only: <c>INodeRuntimeSettings.GetMaxPendingToolCallAgeMinutes</c> can
    ///     override the tool-call age from the database afterwards, and lowering it below this park budget re-opens the gap.
    /// </remarks>
    [Range(1, 3599)]
    public int MaxParkedSeconds { get; init; } = 300;

    /// <summary>The cap on one saved artifact, enforced by the blob store and by the <c>save_artifact</c> tool.</summary>
    [Range(1, 64 * 1024 * 1024)]
    public int MaxArtifactBytes { get; init; } = 1024 * 1024;

    /// <summary>Wall-clock budget for one step. Zero inherits the node's maximum message request timeout.</summary>
    [Range(0, 24 * 60 * 60)]
    public int StepTimeoutSeconds { get; init; }

    /// <summary>
    ///     How large the transcript one step replays may get, in estimated tokens, before the step boundary folds the
    ///     older turns into the conversation synopsis. Zero disables the bound.
    /// </summary>
    /// <remarks>
    ///     Deliberately a flat budget rather than a fraction of the model's context window: what consumes a research step is its own
    ///     tool loop, so the transcript's job is to stay out of the way while the state block, rebuilt from the database every step,
    ///     carries the session's state forward. See <c>docs/wiki/04-agent-mode.md</c> §5.6.
    /// </remarks>
    [Range(0, 1_000_000)]
    public int StepContextBudgetTokens { get; init; } = 12_000;

    /// <summary>
    ///     Tool-result character ceiling for a session step, tightening the node-wide
    ///     <c>Agent:ToolPipeline:MaxToolResultCharacters</c> (65,536) for the duration of the turn. Zero leaves the
    ///     node value in place.
    /// </summary>
    /// <remarks>
    ///     Tighten-only: a value above the node ceiling has no effect. The node value exceeds <c>read_document</c>'s own
    ///     50,000-character cap, so nothing clips a single knowledge-base read; several of them in one research step is
    ///     what overruns a 64k window. See <c>docs/wiki/04-agent-mode.md</c> §5.6.
    /// </remarks>
    [Range(0, 1_000_000)]
    public int MaxToolResultCharacters { get; init; } = 8_000;

    /// <summary>
    ///     How many raw provider rounds one step may make, tightening the node-wide
    ///     <c>Agent:ProviderCallBudget:MaxProviderCallsPerInvocation</c> (200) for the duration of the turn. Zero
    ///     leaves the node value.
    /// </summary>
    /// <remarks>
    ///     The function-invocation loop re-sends every prior tool result and reasoning block each iteration, so a step's context grows
    ///     QUADRATICALLY in its own tool calls and only a cap on the iterations reaches that — neither the step-boundary fold nor the
    ///     per-result cap does. Hitting it ends the step cleanly and the next resumes from the state block. The default is a guess to
    ///     be replaced by a measurement: <see cref="WorkSessionStepConsumptionDetail" /> records what each step spent, so size this
    ///     from the distribution those rows show for the session kind in question. See <c>docs/wiki/04-agent-mode.md</c> §5.6.
    /// </remarks>
    [Range(0, 10_000)]
    public int MaxProviderCallsPerStep { get; init; } = 10;
}
