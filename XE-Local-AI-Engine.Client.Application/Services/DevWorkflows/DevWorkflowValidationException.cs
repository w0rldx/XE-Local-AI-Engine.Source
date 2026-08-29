namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Bad input to the workflow runtime: an unparseable or incoherent graph, a run started against a work item that
///     cannot carry it. The third of the three exception types that cross the run service's boundary, alongside
///     <c>DevWorkflowNotFoundException</c> and <c>DevWorkflowInvalidTransitionException</c> from the store; the API layer
///     maps it to 400.
/// </summary>
public sealed class DevWorkflowValidationException(string message) : InvalidOperationException(message);
