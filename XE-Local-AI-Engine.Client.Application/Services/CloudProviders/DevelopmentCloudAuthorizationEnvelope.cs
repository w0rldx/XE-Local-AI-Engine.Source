namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Code-owned authorization metadata for one Development attempt. The type is sealed and exposes only scalar,
///     getter-only values so the same instance can be carried safely through shallow <c>ChatOptions</c> clones.
/// </summary>
public sealed class DevelopmentCloudAuthorizationEnvelope
{
    public const int CurrentVersion = 1;

    public DevelopmentCloudAuthorizationEnvelope(int version,
        string projectId,
        string taskId,
        string attemptId,
        DevelopmentExecutionPolicy policy,
        string? authorizedBundleId,
        string? authorizedBundleHash,
        DateTimeOffset expiresAt,
        string nonce)
    {
        Version = version;
        ProjectId = projectId;
        TaskId = taskId;
        AttemptId = attemptId;
        Policy = policy;
        AuthorizedBundleId = authorizedBundleId;
        AuthorizedBundleHash = authorizedBundleHash;
        ExpiresAt = expiresAt;
        Nonce = nonce;
    }

    public int Version { get; }
    public string ProjectId { get; }
    public string TaskId { get; }
    public string AttemptId { get; }
    public DevelopmentExecutionPolicy Policy { get; }
    public string? AuthorizedBundleId { get; }
    public string? AuthorizedBundleHash { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string Nonce { get; }
}
