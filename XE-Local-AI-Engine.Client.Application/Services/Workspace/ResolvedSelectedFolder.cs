namespace XE_Local_AI_Engine.Client.Services.Workspace;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Trusted, worker-internal resolution of a selected folder id to its host path. Returned by
///     <see cref="ISelectedFolderResolver.ResolveAsync" /> for workspace copy (workspace copy). Never surfaced to the model.
/// </summary>
public sealed record ResolvedSelectedFolder
{
    public required Guid Id { get; init; }

    public required string Alias { get; init; }

    public required string HostPath { get; init; }

    public required SelectedFolderMode Mode { get; init; }
}
