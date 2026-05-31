namespace XE_Local_AI_Engine.Client.Persistence;

using System.Diagnostics.CodeAnalysis;

/// <summary>
///     Execution shape of an agent definition. <see cref="Single" /> runs the single-agent loop;
///     <see cref="Orchestrator" /> is persisted but executes as a single agent in P3 (orchestration is a later
///     loop marker — the topology column round-trips without changing runtime behavior).
/// </summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "Single is the domain term for a non-orchestrated agent; the overlap with System.Single is incidental.")]
/// <summary>
///     Enumerates supported agent definition kind values.
/// </summary>
public enum AgentDefinitionKind
{
    Single = 0,
    Orchestrator = 1
}
