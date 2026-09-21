namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>The node's always-on tool names — the tools a relevance filter may never hide.</summary>
/// <remarks>
///     Declared here but implemented in the application layer, because composing the set needs two things this assembly
///     cannot reach: the work-session tool catalog, and the <c>Source</c> tag only the node's tool catalog carries. This
///     assembly therefore only ever consumes a set of names. MCP and custom tools are deliberately NOT core — they rank
///     like every other non-core tool, and hiding one changes nothing about calling it, its approval wrap being applied
///     at registry build.
/// </remarks>
public interface IToolRelevanceCoreSet
{
    /// <summary>The always-on names, compared ordinally. Deterministic for the node: identical across agents and turns.</summary>
    IReadOnlySet<string> GetCoreToolNames();
}
