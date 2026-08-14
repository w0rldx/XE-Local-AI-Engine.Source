namespace XE_Local_AI_Engine.Client.Services.Invocation;

using System.Security.Cryptography;

/// <summary>
///     Represents invocation execution context.
/// </summary>
public sealed class InvocationExecutionContext : IDisposable
{
    private byte[]? _ownedEpochKey;

    public required global::XE_Local_AI_Engine.Client.Models.RuntimePackage Package { get; init; }

    public required Guid MessageId { get; init; }

    public required int EpochVersion { get; init; }

    public required ReadOnlyMemory<byte> EpochKey { get; init; }

    public required bool IsEncrypted { get; init; }

    /// <summary>
    ///     Optional post-warm, pre-generation admission policy. Existing chat, scheduler, and platform callers leave it
    ///     unset and retain their current behavior.
    /// </summary>
    public IInvocationGenerationAdmissionPolicy? GenerationAdmissionPolicy { get; init; }

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

    public static InvocationExecutionContext Create(global::XE_Local_AI_Engine.Client.Models.RuntimePackage package,
        Guid messageId,
        int epochVersion,
        ReadOnlyMemory<byte> epochKey,
        IInvocationGenerationAdmissionPolicy? generationAdmissionPolicy = null)
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
            GenerationAdmissionPolicy = generationAdmissionPolicy,
            _ownedEpochKey = ownedEpochKey
        };
    }

    public static InvocationExecutionContext CreatePlain(global::XE_Local_AI_Engine.Client.Models.RuntimePackage package,
        Guid messageId,
        long? harnessStartedTimestamp = null,
        double? preRunDurationMs = null,
        double? queueDurationMs = null,
        IInvocationGenerationAdmissionPolicy? generationAdmissionPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(package);

        return new InvocationExecutionContext
        {
            Package = package,
            MessageId = messageId,
            EpochVersion = 0,
            EpochKey = ReadOnlyMemory<byte>.Empty,
            IsEncrypted = false,
            GenerationAdmissionPolicy = generationAdmissionPolicy,
            HarnessStartedTimestamp = harnessStartedTimestamp,
            PreRunDurationMs = preRunDurationMs,
            QueueDurationMs = queueDurationMs
        };
    }
}
