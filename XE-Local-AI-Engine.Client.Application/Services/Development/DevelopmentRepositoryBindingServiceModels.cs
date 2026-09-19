namespace XE_Local_AI_Engine.Client.Services.Development;

public sealed record DevelopmentRepositoryBinding
{
    public required Guid ProjectId { get; init; }

    public required Guid SelectedFolderId { get; init; }

    public required string Alias { get; init; }

    public required string RepositoryRoot { get; init; }

    public required string RepositoryIdentityHash { get; init; }
}

public sealed class DevelopmentRepositoryReference
{
    public required string Id { get; init; }

    public required string Alias { get; init; }

    public required string Availability { get; init; }
}
