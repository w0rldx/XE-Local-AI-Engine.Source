namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

/// <summary>
///     What <see cref="DevWorkflowToolCommands.OverlayAsync" /> did: what the report should say it judged, or the
///     sanitized sentence that refuses the node because the child's work could not be put in front of it honestly.
/// </summary>
/// <remarks>
///     <see cref="Patch" /> is the exact bytes that were overlaid, carried back for the lane to publish, and empty when
///     nothing was overlaid or the patch is over the artifact limit (<see cref="DevWorkflowValidationBasedOn.PatchArtifactOmitted" /> says so).
///     <see cref="PatchModelId" /> is the model the Dev Mode coder attempt that produced the patch ran on, when that attempt is recorded.
/// </remarks>
internal readonly record struct DevWorkflowOverlay
{
    public required DevWorkflowValidationBasedOn? BasedOn { get; init; }

    public required string? Refusal { get; init; }

    public ReadOnlyMemory<byte> Patch { get; init; }

    public string? PatchModelId { get; init; }
}
