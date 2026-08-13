namespace XE_Local_AI_Engine.Client.Services.Invocation;

using System.Security.Cryptography;

/// <summary>
///     Represents invocation execution context.
/// </summary>
public sealed class InvocationExecutionContext : IDisposable
{
    private byte[]? _ownedEpochKey;

    public required Models.RuntimePackage Package { get; init; }

    public required Guid MessageId { get; init; }

    public required int EpochVersion { get; init; }

    public required ReadOnlyMemory<byte> EpochKey { get; init; }

    public required bool IsEncrypted { get; init; }

    /// <summary>
    ///     Optional monotonic timestamp captured by the product entry path before chat admission/context/persistence.
    ///     The runner uses it only for end-to-end harness latency; external/platform invocations leave it unset and use
    ///     the runner-entry timestamp.
    /// </summary>
    public long? HarnessStartedTimestamp { get; init; }

    /// <summary>Elapsed pre-run chat admission/context/persistence time, when supplied by the local chat path.</summary>
    public double? PreRunDurationMs { get; init; }

    /// <summary>Elapsed collision-slot queue time, when supplied by the local chat path.</summary>
    public double? QueueDurationMs { get; init; }

    public void Dispose()
    {
        if (_ownedEpochKey is not null)
        {
            CryptographicOperations.ZeroMemory(_ownedEpochKey);
            _ownedEpochKey = null;
        }
    }

    public static InvocationExecutionContext Create(Models.RuntimePackage package, Guid messageId, int epochVersion, ReadOnlyMemory<byte> epochKey)
    {
        ArgumentNullException.ThrowIfNull(package);

        var ownedEpochKey = epochKey.ToArray();
        var isEncrypted = ownedEpochKey.Length > 0;

        return new InvocationExecutionContext
        {
            Package = package,
            MessageId = messageId,
            EpochVersion = epochVersion,
            EpochKey = ownedEpochKey,
            IsEncrypted = isEncrypted,
            _ownedEpochKey = ownedEpochKey
        };
    }

    public static InvocationExecutionContext CreatePlain(Models.RuntimePackage package,
        Guid messageId,
        long? harnessStartedTimestamp = null,
        double? preRunDurationMs = null,
        double? queueDurationMs = null)
    {
        ArgumentNullException.ThrowIfNull(package);

        return new InvocationExecutionContext
        {
            Package = package,
            MessageId = messageId,
            EpochVersion = 0,
            EpochKey = ReadOnlyMemory<byte>.Empty,
            IsEncrypted = false,
            HarnessStartedTimestamp = harnessStartedTimestamp,
            PreRunDurationMs = preRunDurationMs,
            QueueDurationMs = queueDurationMs
        };
    }
}
