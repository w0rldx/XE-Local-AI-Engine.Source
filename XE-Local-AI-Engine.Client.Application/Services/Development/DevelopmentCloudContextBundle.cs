namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Collections.ObjectModel;

public sealed record DevelopmentCloudContextExcerpt(string RelativePath, string Content);

/// <summary>
///     Immutable, content-addressed context approved for one cloud Development attempt.
/// </summary>
public sealed class DevelopmentCloudContextBundle
{
    private readonly ReadOnlyCollection<DevelopmentCloudContextExcerpt> _excerpts;

    internal DevelopmentCloudContextBundle(string id,
        string projectId,
        string taskId,
        string attemptId,
        string providerName,
        string modelId,
        string requirements,
        string acceptanceCriteria,
        string policyText,
        IEnumerable<DevelopmentCloudContextExcerpt> excerpts,
        string contentHash,
        long byteCount,
        int estimatedTokenCount,
        DateTimeOffset expiresAt,
        string nonce,
        bool secretScanPassed)
    {
        Id = id;
        ProjectId = projectId;
        TaskId = taskId;
        AttemptId = attemptId;
        ProviderName = providerName;
        ModelId = modelId;
        Requirements = requirements;
        AcceptanceCriteria = acceptanceCriteria;
        PolicyText = policyText;
        _excerpts = Array.AsReadOnly(excerpts.ToArray());
        ContentHash = contentHash;
        ByteCount = byteCount;
        EstimatedTokenCount = estimatedTokenCount;
        ExpiresAt = expiresAt;
        Nonce = nonce;
        SecretScanPassed = secretScanPassed;
    }

    public string Id { get; }
    public string ProjectId { get; }
    public string TaskId { get; }
    public string AttemptId { get; }
    public string ProviderName { get; }
    public string ModelId { get; }
    public string Requirements { get; }
    public string AcceptanceCriteria { get; }
    public string PolicyText { get; }
    public IReadOnlyList<DevelopmentCloudContextExcerpt> Excerpts => _excerpts;
    public string ContentHash { get; }
    public long ByteCount { get; }
    public int EstimatedTokenCount { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string Nonce { get; }
    public bool SecretScanPassed { get; }

    public string ReadResource(string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        return resource switch
        {
            "requirements" => Requirements,
            "acceptance-criteria" => AcceptanceCriteria,
            "policy" => PolicyText,
            _ when resource.StartsWith("excerpt:", StringComparison.Ordinal) => ReadExcerpt(resource["excerpt:".Length..]),
            _ => throw new KeyNotFoundException("The requested resource is not present in the approved Development cloud context bundle.")
        };
    }

    private string ReadExcerpt(string relativePath)
    {
        var excerpt = _excerpts.SingleOrDefault(item => string.Equals(item.RelativePath, relativePath, StringComparison.Ordinal));
        return excerpt?.Content
               ?? throw new KeyNotFoundException("The requested excerpt is not present in the approved Development cloud context bundle.");
    }
}
