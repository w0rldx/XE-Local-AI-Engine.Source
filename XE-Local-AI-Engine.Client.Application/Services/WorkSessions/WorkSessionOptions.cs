namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Configuration for agent work sessions.
///     <para>
///         The shipped posture, stated here once so nothing else has to restate it: <c>XE-Local-AI-Engine.Client/appsettings.json</c>
///         ships <c>WorkSessions:Enabled: true</c>, and the feature is on for every node that does not override the key.
///         The compiled-in default of <see cref="Enabled" /> is <see langword="false" />, which is what a host binding
///         a configuration source WITHOUT the key gets — most test hosts, and nothing that ships.
///     </para>
///     <para>
///         <see cref="Enabled" /> gates <em>behaviour</em>, never registration: the endpoints and the hub are mapped
///         unconditionally (see <c>WorkSessionEndpoints</c>), so an empty container would answer 500 where a disabled
///         node has to answer legibly. A disabled node answers <c>404</c> from request-path middleware that runs ahead
///         of authentication, never <c>500</c> — pinned by
///         <c>WorkSessionEndpointTests.WorkSessionRoute_WhenTheFeatureIsDisabled_ReturnsNotFoundWithoutReachingTheService</c>.
///     </para>
/// </summary>
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
    ///     How long a session may sit waiting on an approval or a question before it is demoted to <c>Paused</c>. An
    ///     unattended parked session would otherwise hold the node's only invocation slot indefinitely.
    ///     <para>
    ///         Must stay strictly under <c>WorkerNode:MaxPendingToolCallAgeMinutes</c> (in minutes; 10 by default, so
    ///         600 seconds), or the node expires the pending tool call the session is parked on before the park clock
    ///         fires and the park times out against a prompt that can no longer be answered.
    ///         <c>WorkSessionOptionsValidator</c> checks the relation at startup against the configured seed. The upper
///         bound of the range below is 3599 because the node's tool-call age itself caps at 60 minutes
///         (<c>StoredNodeSettings.MaxMaxPendingToolCallAgeMinutes</c>), so nothing above that could ever validate.
    ///     </para>
    ///     <para>
    ///         follow-up: that check reads the configured value only. <c>INodeRuntimeSettings.GetMaxPendingToolCallAgeMinutes</c>
    ///         lets the database override the tool-call age at runtime, and a startup check cannot see a value written
    ///         after it ran — an operator who lowers it below this park budget re-opens the gap.
    ///     </para>
    /// </summary>
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
    ///     <para>
    ///         Deliberately a flat budget rather than a fraction of the model's context window. What consumes a
    ///         research step is its own tool loop — a single <c>read_document</c> is capped at 50,000 characters, some
    ///         16k tokens — so the transcript's job is to stay out of the way, and the state block (rebuilt from the
    ///         database on every step) is what actually carries the session's state forward. The default leaves the
    ///         large majority of a 64k window to the step itself.
    ///     </para>
    /// </summary>
    [Range(0, 1_000_000)]
    public int StepContextBudgetTokens { get; init; } = 12_000;

    /// <summary>
    ///     Tool-result character ceiling for a session step, tightening the node-wide
    ///     <c>Agent:ToolPipeline:MaxToolResultCharacters</c> (65,536) for the duration of the turn. Zero leaves the node
    ///     value in place.
    ///     <para>
    ///         The node value is larger than <c>read_document</c>'s own 50,000-character cap, so nothing clips a
    ///         knowledge-base read today; several of them in one research step is what overran a 65,536-token window.
    ///         The default of 8,000 (~2–2.5k tokens per result, at the ~3.4–3.6 chars/token this corpus actually runs)
    ///         clips a full-size knowledge-base read to about a sixth of itself and leaves a step room for several of
    ///         them. Tighten-only: a value above the node ceiling has no effect.
    ///     </para>
    /// </summary>
    [Range(0, 1_000_000)]
    public int MaxToolResultCharacters { get; init; } = 8_000;

    /// <summary>
    ///     How many raw provider rounds one step may make, tightening the node-wide
    ///     <c>Agent:ProviderCallBudget:MaxProviderCallsPerInvocation</c> (200) for the duration of the turn. Zero leaves
    ///     the node value.
    ///     <para>
    ///         The function-invocation loop re-sends every prior tool result and every reasoning block on each
    ///         iteration, so a step's context grows QUADRATICALLY in its own tool calls — 14 calls in one step is what
    ///         overran a 65,536-token window on 2026-08-24, with each individual result already clipped. Neither the
    ///         step-boundary fold nor the per-result cap can reach that; only a cap on the iterations can. Hitting it
    ///         ends the step cleanly and the next one resumes from the state block, so the work continues.
    ///     </para>
    ///     <para>
    ///         The default of 10 was a guess, and it is meant to be replaced by a measurement rather than by another
    ///         one. Every step now records what it spent on its <c>StepEnded</c> / <c>StepFailed</c> row — see
    ///         <see cref="WorkSessionStepConsumptionDetail" />, which carries the admitted call count against this cap,
    ///         the tool calls, and the estimated input tokens — so size this from the distribution those rows show for
    ///         the session kind in question.
    ///     </para>
    /// </summary>
    [Range(0, 10_000)]
    public int MaxProviderCallsPerStep { get; init; } = 10;
}
