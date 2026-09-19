namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

public sealed record DevelopmentCloudEgressAudit
{
    public required string ProjectId { get; init; }

    public required string TaskId { get; init; }

    public required string AttemptId { get; init; }

    public required string ProviderName { get; init; }

    public required string ModelId { get; init; }

    public required string BundleId { get; init; }

    public required string BundleHash { get; init; }

    public required DateTimeOffset AuthorizedAt { get; init; }
}

public interface IDevelopmentCloudEgressAuditSink
{
    void Record(DevelopmentCloudEgressAudit audit);
}
