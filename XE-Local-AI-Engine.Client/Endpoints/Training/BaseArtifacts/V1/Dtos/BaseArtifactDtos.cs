namespace XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1;

using System.ComponentModel.DataAnnotations;

public sealed class CreateBaseArtifactRequest
{
    /// <summary>The Hugging Face base checkpoint repository, e.g. <c>unsloth/Llama-3.2-1B-Instruct</c>.</summary>
    [Required]
    [StringLength(200, MinimumLength = 3)]
    public required string RepoId { get; init; }

    /// <summary>Optional commit or branch to pin. Omitted means the repository's current default revision.</summary>
    [StringLength(100)]
    public string? Revision { get; init; }
}

public sealed class BaseArtifactFileResponse
{
    public required string Role { get; init; }
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public string? Sha256 { get; init; }
}

public sealed class BaseArtifactLicenseResponse
{
    public required string RepoId { get; init; }
    public string? License { get; init; }
    public required bool IsGated { get; init; }
    public required long FetchedAtUtc { get; init; }
}

public sealed class BaseArtifactProgressResponse
{
    public required long CompletedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public required int FileIndex { get; init; }
    public required int FileCount { get; init; }
}

public sealed class BaseArtifactResponse
{
    public required Guid Id { get; init; }
    public required string RepoId { get; init; }
    public required string Revision { get; init; }
    public required string Status { get; init; }
    public required long TotalBytes { get; init; }
    public required IReadOnlyList<BaseArtifactFileResponse> Files { get; init; }
    public BaseArtifactLicenseResponse? License { get; init; }
    public string? ErrorMessage { get; init; }
    public required long Version { get; init; }
    public required long CreatedAtUtc { get; init; }
    public required long UpdatedAtUtc { get; init; }
    public BaseArtifactProgressResponse? Progress { get; init; }
}

public sealed class BaseArtifactListResponse
{
    public required IReadOnlyList<BaseArtifactResponse> Items { get; init; }
}

public sealed class BaseArtifactBlockedResponse
{
    public required string Reason { get; init; }
    public required string Message { get; init; }
}
