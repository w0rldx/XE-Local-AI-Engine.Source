namespace XE_Local_AI_Engine.Client.Services.Development;

public sealed class DevelopmentCloudContextBuildRequest
{
    public required string BundleId { get; init; }

    public required string ProjectId { get; init; }

    public required string TaskId { get; init; }

    public required string AttemptId { get; init; }

    public required string ProviderName { get; init; }

    public required string ModelId { get; init; }

    public required string Requirements { get; init; }

    public required string AcceptanceCriteria { get; init; }

    public required string PolicyText { get; init; }

    public required IReadOnlyList<DevelopmentCloudContextExcerpt> Excerpts { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required string Nonce { get; init; }
}
