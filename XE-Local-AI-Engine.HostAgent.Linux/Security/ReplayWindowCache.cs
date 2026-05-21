namespace XE_Local_AI_Engine.HostAgent.Linux.Security;

public sealed class ReplayWindowCache
{
    private readonly Dictionary<long, BucketEntries> _buckets = [];
    private readonly Lock _gate = new();

    public bool TryRegister(long bucket, long currentBucket, string requestId, int maxRequestIdsPerBucket)
    {
        lock (_gate)
        {
            RemoveExpiredBuckets(currentBucket);

            if (!_buckets.TryGetValue(bucket, out var entries))
            {
                entries = new BucketEntries();
                _buckets.Add(bucket, entries);
            }

            if (!entries.RequestIds.Add(requestId))
            {
                return false;
            }

            entries.Order.Enqueue(requestId);
            Trim(entries, maxRequestIdsPerBucket);
            return true;
        }
    }

    private void RemoveExpiredBuckets(long currentBucket)
    {
        foreach (var expiredBucket in _buckets.Keys.Where(bucket => bucket < currentBucket).ToArray())
        {
            _buckets.Remove(expiredBucket);
        }
    }

    private static void Trim(BucketEntries entries, int maxRequestIdsPerBucket)
    {
        while (entries.Order.Count > maxRequestIdsPerBucket && entries.Order.TryDequeue(out var requestId))
        {
            entries.RequestIds.Remove(requestId);
        }
    }

    private sealed class BucketEntries
    {
        public HashSet<string> RequestIds { get; } = new(StringComparer.Ordinal);

        public Queue<string> Order { get; } = new();
    }
}
