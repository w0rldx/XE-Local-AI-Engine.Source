namespace XE_Local_AI_Engine.Client.Services.Development;

public sealed record DevelopmentRepositoryBinding(
    Guid ProjectId,
    Guid SelectedFolderId,
    string Alias,
    string RepositoryRoot,
    string RepositoryIdentityHash);

public sealed record DevelopmentRepositoryReference(string Id, string Alias, string Availability);
