namespace XE_Local_AI_Engine.Client.Services.Invocation;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Client.Models;

public sealed class InvocationExecutionContext : IDisposable
{
    private byte[]? _ownedEpochKey;

    public required RuntimePackage Package { get; init; }

    public required Guid MessageId { get; init; }

    public required int EpochVersion { get; init; }

    public required ReadOnlyMemory<byte> EpochKey { get; init; }

    public static InvocationExecutionContext Create(RuntimePackage package, Guid messageId, int epochVersion, ReadOnlyMemory<byte> epochKey)
    {
        ArgumentNullException.ThrowIfNull(package);

        var ownedEpochKey = epochKey.ToArray();

        return new InvocationExecutionContext
        {
            Package = package,
            MessageId = messageId,
            EpochVersion = epochVersion,
            EpochKey = ownedEpochKey,
            _ownedEpochKey = ownedEpochKey
        };
    }

    public void Dispose()
    {
        if (_ownedEpochKey is not null)
        {
            CryptographicOperations.ZeroMemory(_ownedEpochKey);
            _ownedEpochKey = null;
        }
    }
}
