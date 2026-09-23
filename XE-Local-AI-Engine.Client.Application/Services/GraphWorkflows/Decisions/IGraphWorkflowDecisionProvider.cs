namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Decisions;

using System.Text.Json;

/// <summary>
///     One way a <c>DecisionModel</c> node reaches its choice. The node names a provider by <see cref="Name" /> from the
///     parser's closed vocabulary (<c>GraphWorkflowGraph.DecisionProviders</c>).
/// </summary>
/// <remarks>
///     A provider does not run anything itself: it LOWERS the node to an LLM call config, which the invocation lane
///     already knows how to run end to end (model gate, capacity, bindings, grammar), and INTERPRETS what came back.
///     The executor checks the choice against the node's labels, so no provider can route outside them.
/// </remarks>
internal interface IGraphWorkflowDecisionProvider
{
    string Name { get; }

    GraphWorkflowLlmCallConfig Lower(GraphWorkflowDecisionModelConfig config);

    DecisionResult Interpret(string text, JsonElement? json);
}
