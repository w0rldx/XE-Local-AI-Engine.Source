namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;

/// <summary>
///     The compiled, MAF-agnostic input to <see cref="IOrchestrationAgentFactory" />: the triage agent, the full
///     participant set (which INCLUDES the triage), the handoff edges, and the workflow knobs. The resolver
///     produces this 1:1 from a <c>Kind=Orchestrator</c> agent definition's topology; the factory turns it
///     into a handoff <c>Workflow</c> and a drive session, confining all <c>Microsoft.Agents.AI.Workflows</c> types
///     to this assembly.
/// </summary>
public sealed record OrchestrationAgentDefinition
{
    /// <summary>The coordinator that receives the workflow input. Also present in <see cref="Participants" />.</summary>
    public required OrchestrationParticipant Triage { get; init; }

    /// <summary>All participants, including <see cref="Triage" />. Must contain at least the triage.</summary>
    public required IReadOnlyList<OrchestrationParticipant> Participants { get; init; }

    /// <summary>
    ///     The directed handoff edges. An EMPTY list means mesh-default: every participant is registered and MAF
    ///     auto-wires every agent to hand off to every other.
    /// </summary>
    public required IReadOnlyList<OrchestrationEdge> Edges { get; init; }

    /// <summary>
    ///     When true the factory emits per-token <c>AgentResponseUpdateEvent</c>s (streaming deltas) in addition to
    ///     the aggregated per-turn events. Maps to the existing streaming chat UX.
    /// </summary>
    public bool EmitStreamingUpdates { get; init; } = true;

    /// <summary>
    ///     Autonomous-mode per-agent turn cap (maps to MAF's <c>WithAutonomousMode</c>). When &gt; 0 an agent whose response
    ///     contains no handoff is re-invoked up to this many times; the loop ends on a handoff, the termination
    ///     condition, or the cap. 0 disables autonomous mode (every user turn re-enters via triage).
    /// </summary>
    public int MaxTurnsPerAgent { get; init; }

    /// <summary>
    ///     When true, subsequent user turns route back to the specialist that handled the previous turn rather than
    ///     always re-entering through triage (maps to MAF's <c>EnableReturnToPrevious</c>).
    /// </summary>
    public bool ReturnToPrevious { get; init; }
}
