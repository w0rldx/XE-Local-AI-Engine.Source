namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Configuration for agent work sessions.
///     <para>
///         <see cref="Enabled" /> gates <em>behaviour</em>, never registration: the endpoints and the hub are mapped
///         unconditionally, so an empty container would answer 500 where a disabled node has to answer legibly.
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
    /// </summary>
    [Range(1, 24 * 60 * 60)]
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
    ///         The default of 16,000 (~4–5k tokens per result) leaves a step room for three or four reads. Tighten-only:
    ///         a value above the node ceiling has no effect.
    ///     </para>
    /// </summary>
    [Range(0, 1_000_000)]
    public int MaxToolResultCharacters { get; init; } = 16_000;
}
