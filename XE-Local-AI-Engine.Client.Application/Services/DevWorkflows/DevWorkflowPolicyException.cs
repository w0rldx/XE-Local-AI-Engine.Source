namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     A refusal a HUMAN has to answer rather than a misconfiguration: what the node was asked to do is coherent, and
///     policy says no to doing it. Distinct from <see cref="DevWorkflowValidationException" /> because the lane maps
///     the two to different failure classes — <c>Configuration</c> names something an author must fix, and a policy
///     refusal named that way would send an operator looking for a mistake in a definition that has none.
/// </summary>
public sealed class DevWorkflowPolicyException(string message) : InvalidOperationException(message);
