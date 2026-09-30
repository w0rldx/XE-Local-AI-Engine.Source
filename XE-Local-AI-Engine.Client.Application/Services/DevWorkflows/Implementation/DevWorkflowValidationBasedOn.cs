namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

/// <summary>What the commands ran against when the base commit alone would not say it.</summary>
/// <remarks>
///     The upstream implementation task whose approved patch was overlaid onto the workspace first. Absent means
///     nothing was overlaid: either this node has no upstream implementation to judge, or the pass was refused.
/// </remarks>
internal sealed record DevWorkflowValidationBasedOn(Guid DevelopmentTaskId, string PatchHash, string Detail);
