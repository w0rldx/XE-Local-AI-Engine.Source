namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

/// <summary>
///     What <see cref="DevWorkflowToolCommands.OverlayAsync" /> did: what the report should say it judged, or the
///     sanitized sentence that refuses the node because the child's work could not be put in front of it honestly.
/// </summary>
internal readonly record struct DevWorkflowOverlay(DevWorkflowValidationBasedOn? BasedOn, string? Refusal);
