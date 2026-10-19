namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Decisions;

/// <summary>
///     A decision provider that answers itself, through a model built for it, rather than lowering the node to an LLM
///     call like <see cref="IGraphWorkflowDecisionProvider" />.
/// </summary>
/// <remarks>
///     The executor gates the model, binds the input and admits capacity before calling it, and holds the answer to the
///     node's question type and labels afterwards; the provider only asks and reports.
/// </remarks>
internal interface IGraphWorkflowNativeDecisionProvider
{
    string Name { get; }

    Task<DecisionResult> DecideAsync(NativeDecisionRequest request, CancellationToken cancellationToken);
}
