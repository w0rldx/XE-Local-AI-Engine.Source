namespace XE_Local_AI_Engine.AI.Agent.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Options for the multi-agent handoff orchestration runtime. A handoff run goes IDLE (rather than halting)
///     after it yields its terminal output, so the drive session bounds each watch with an idle timeout; this is
///     the per-quiescence ceiling, not a wall-clock cap on the whole run (the runner's invocation cancellation
///     token still governs overall lifetime).
/// </summary>
public sealed class OrchestrationAgentOptions
{
    public const string Section = "Agent:Orchestration";

    /// <summary>
    ///     Seconds the drive session waits on a quiet (idle) run before treating the turn as complete. Must be
    ///     positive. A value large enough to span a model's slowest single turn; the run is otherwise bounded by the
    ///     caller's cancellation token.
    /// </summary>
    [Range(1, 3600)]
    public int IdleTimeoutSeconds { get; set; } = 120;
}
