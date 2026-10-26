namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

/// <summary>
///     What <see cref="DevWorkflowToolCommands.OverlayAsync" /> did: what the report should say it judged, or the
///     sanitized sentence that refuses the node because the child's work could not be put in front of it honestly.
/// </summary>
/// <remarks>
///     <see cref="Patch" /> is the exact bytes that were overlaid, carried back for the lane to publish, and empty when
///     nothing was overlaid or the patch is over the artifact limit (<see cref="DevWorkflowValidationBasedOn.PatchArtifactOmitted" /> says so).
/// </remarks>
internal readonly record struct DevWorkflowOverlay(DevWorkflowValidationBasedOn? BasedOn, string? Refusal, ReadOnlyMemory<byte> Patch = default);
