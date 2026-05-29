namespace XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Model-facing view of a selected folder. Carries only the opaque <see cref="Id" /> and <see cref="Alias" /> —
///     never the host path. This is the shape exposed to the agent / tool surface.
/// </summary>
public sealed record SelectedFolderReference(string Id, string Alias);
