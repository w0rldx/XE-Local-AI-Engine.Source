namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using Microsoft.Extensions.Logging;

public sealed partial class LoggingDevelopmentCloudEgressAuditSink(ILogger<LoggingDevelopmentCloudEgressAuditSink> logger)
    : IDevelopmentCloudEgressAuditSink
{
    private readonly ILogger<LoggingDevelopmentCloudEgressAuditSink> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public void Record(DevelopmentCloudEgressAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        CloudEgressAuthorized(_logger,
            audit.ProjectId,
            audit.TaskId,
            audit.AttemptId,
            audit.ProviderName,
            audit.ModelId,
            audit.BundleId,
            audit.BundleHash,
            audit.AuthorizedAt);
    }

    [LoggerMessage(EventId = 4701,
        Level = LogLevel.Information,
        Message = "Development cloud egress authorized for project {ProjectId}, task {TaskId}, attempt {AttemptId}, provider {ProviderName}, model {ModelId}, bundle {BundleId}, hash {BundleHash}, at {AuthorizedAt}.")]
    private static partial void CloudEgressAuthorized(ILogger logger,
        string projectId,
        string taskId,
        string attemptId,
        string providerName,
        string modelId,
        string bundleId,
        string bundleHash,
        DateTimeOffset authorizedAt);
}
