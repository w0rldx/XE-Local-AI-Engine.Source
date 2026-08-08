namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Collections.Concurrent;

public interface IDevelopmentCloudContextCatalog
{
    void Register(DevelopmentCloudContextBundle bundle);
    bool TryGet(string bundleId, out DevelopmentCloudContextBundle? bundle);
}

public sealed class DevelopmentCloudContextCatalog : IDevelopmentCloudContextCatalog
{
    private readonly ConcurrentDictionary<string, DevelopmentCloudContextBundle> _bundles = new(StringComparer.Ordinal);

    public void Register(DevelopmentCloudContextBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (_bundles.TryAdd(bundle.Id, bundle))
        {
            return;
        }

        var existing = _bundles[bundle.Id];
        if (!HasSameAuthorization(existing, bundle))
        {
            throw new InvalidOperationException("An approved Development cloud context bundle with this identifier already exists.");
        }
    }

    public bool TryGet(string bundleId, out DevelopmentCloudContextBundle? bundle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleId);
        return _bundles.TryGetValue(bundleId, out bundle);
    }

    private static bool HasSameAuthorization(DevelopmentCloudContextBundle left, DevelopmentCloudContextBundle right)
    {
        return string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal)
               && string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal)
               && string.Equals(left.AttemptId, right.AttemptId, StringComparison.Ordinal)
               && string.Equals(left.ProviderName, right.ProviderName, StringComparison.Ordinal)
               && string.Equals(left.ModelId, right.ModelId, StringComparison.Ordinal)
               && string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal)
               && left.ByteCount == right.ByteCount
               && left.ExpiresAt == right.ExpiresAt
               && string.Equals(left.Nonce, right.Nonce, StringComparison.Ordinal)
               && left.SecretScanPassed == right.SecretScanPassed;
    }
}
