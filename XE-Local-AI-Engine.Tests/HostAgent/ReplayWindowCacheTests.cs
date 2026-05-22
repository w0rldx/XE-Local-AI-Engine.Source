namespace XE_Local_AI_Engine.Tests.HostAgent;

using System.Globalization;
using XE_Local_AI_Engine.HostAgent.Linux.Security;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ReplayWindowCacheTests
{
    private const long Bucket = 1_000;

    [Test]
    public void TryRegister_WhenRequestIdIsNew_ReturnsTrue()
    {
        var cache = new ReplayWindowCache();

        var result = cache.TryRegister(Bucket, Bucket, "request-a", 16);

        AssertEx.True(result);
    }

    [Test]
    public void TryRegister_WhenRequestIdRepeatsInSameBucket_ReturnsFalse()
    {
        var cache = new ReplayWindowCache();

        var first = cache.TryRegister(Bucket, Bucket, "request-a", 16);
        var second = cache.TryRegister(Bucket, Bucket, "request-a", 16);

        AssertEx.True(first);
        AssertEx.False(second);
    }

    [Test]
    public void TryRegister_WhenBucketExceedsMaxRequestIds_EvictsAndAllowsNew()
    {
        var cache = new ReplayWindowCache();
        const int maxRequestIds = 4;

        for (var index = 0; index < maxRequestIds; index++)
        {
            var added = cache.TryRegister(Bucket, Bucket, RequestId(index), maxRequestIds);
            AssertEx.True(added);
        }

        // Adding one more request id past the cap evicts the oldest entry (FIFO).
        var addedOverflow = cache.TryRegister(Bucket, Bucket, RequestId(maxRequestIds), maxRequestIds);
        AssertEx.True(addedOverflow);

        // The oldest request id was evicted, so it is treated as new again and accepted.
        var oldestAcceptedAgain = cache.TryRegister(Bucket, Bucket, RequestId(0), maxRequestIds);
        AssertEx.True(oldestAcceptedAgain);

        // A request id that is still within the retained window is rejected as a replay.
        var recentRejected = cache.TryRegister(Bucket, Bucket, RequestId(maxRequestIds), maxRequestIds);
        AssertEx.False(recentRejected);
    }

    [Test]
    public void TryRegister_WhenBucketIsBehindCurrent_EvictsExpiredBucketAndAllowsReuse()
    {
        var cache = new ReplayWindowCache();

        var registeredInOldBucket = cache.TryRegister(Bucket, Bucket, "request-a", 16);
        AssertEx.True(registeredInOldBucket);

        // Advancing the current bucket evicts the now-expired bucket, so the same id is accepted in the new bucket.
        var registeredInNewBucket = cache.TryRegister(Bucket + 1, Bucket + 1, "request-a", 16);
        AssertEx.True(registeredInNewBucket);
    }

    private static string RequestId(int index)
    {
        return string.Create(CultureInfo.InvariantCulture, $"request-{index}");
    }
}
