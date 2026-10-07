namespace XE_Local_AI_Engine.AI.Agent.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>Options for the multi-agent handoff orchestration runtime.</summary>
/// <remarks>
///     The idle timeout is a stall bound while the provider is producing output, not a wall-clock cap on the whole
///     run. Waits for a round's first output and server-side tool runs are bounded by the turn deadline (the runner's
///     invocation cancellation token), which also governs overall lifetime.
/// </remarks>
public sealed class OrchestrationAgentOptions
{
    public const string Section = "Agent:Orchestration";

    /// <summary>
    ///     Seconds a run that is producing output may stall before the watch fails as idle. Must be positive. Waits
    ///     for a round's first output and server-side tool runs are bounded by the turn deadline instead.
    /// </summary>
    [Range(minimum: 1, maximum: 3600)]
    public int IdleTimeoutSeconds { get; set; } = 120;
}
