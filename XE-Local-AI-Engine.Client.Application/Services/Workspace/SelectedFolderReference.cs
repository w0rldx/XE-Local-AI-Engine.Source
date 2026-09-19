namespace XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Model-facing view of a selected folder. Carries only the opaque <see cref="Id" /> and <see cref="Alias" /> —
///     never the host path. This is the shape exposed to the agent / tool surface.
/// </summary>
public sealed class SelectedFolderReference
{
    public required string Id { get; init; }

    public required string Alias { get; init; }
}
