namespace XE_Local_AI_Engine.Client.Services.Workspace;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Registration input for a selected folder. The resolver normalizes <see cref="Alias" /> and validates
///     <see cref="HostPath" /> (absolute, traversal-free) before persisting.
/// </summary>
public sealed class SelectedFolderRegistration
{
    public required string Alias { get; init; }

    public required string HostPath { get; init; }

    public SelectedFolderMode Mode { get; init; }
}
