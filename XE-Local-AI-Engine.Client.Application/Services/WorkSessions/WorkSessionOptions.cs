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
}
