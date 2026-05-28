namespace XE_Local_AI_Engine.Client.Services.Invocation;

using System.Security.Cryptography;

public sealed class InvocationExecutionContext : IDisposable
{
    private byte[]? _ownedEpochKey;

    public required Models.RuntimePackage Package { get; init; }

    public required Guid MessageId { get; init; }

    public required int EpochVersion { get; init; }

    public required ReadOnlyMemory<byte> EpochKey { get; init; }

    public required bool IsEncrypted { get; init; }

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

    public static InvocationExecutionContext CreatePlain(Models.RuntimePackage package, Guid messageId)
    {
        ArgumentNullException.ThrowIfNull(package);

        return new InvocationExecutionContext
        {
            Package = package,
            MessageId = messageId,
            EpochVersion = 0,
            EpochKey = ReadOnlyMemory<byte>.Empty,
            IsEncrypted = false
        };
    }
}
