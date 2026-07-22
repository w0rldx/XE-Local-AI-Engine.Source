namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

public sealed record DevelopmentCloudEgressAudit(
    string ProjectId,
    string TaskId,
    string AttemptId,
    string ProviderName,
    string ModelId,
    string BundleId,
    string BundleHash,
    DateTimeOffset AuthorizedAt);

public interface IDevelopmentCloudEgressAuditSink
{
    void Record(DevelopmentCloudEgressAudit audit);
}
