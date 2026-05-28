namespace XE_Local_AI_Engine.Client.Services.Persistence;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence;

public sealed class NodeSqliteKeyHolder : INodeSqliteKeyHolder
{
    private const int ExpectedKeyLength = 32;
    private static readonly byte[] EmptySalt = [];

    private byte[]? _key;

    public NodeSqliteKeyHolder(IOptions<WorkerNodeOptions> workerNodeOptions, INodeOperatorSecretProvider operatorSecretProvider)
    {
        ArgumentNullException.ThrowIfNull(workerNodeOptions);
        ArgumentNullException.ThrowIfNull(operatorSecretProvider);

        var nodeName = workerNodeOptions.Value.NodeName;
        if (string.IsNullOrWhiteSpace(nodeName))
        {
            throw new InvalidOperationException("WorkerNode:NodeName must be configured.");
        }

        var operatorSecret = operatorSecretProvider.GetOperatorSecret();

        try
        {
            _key = DeriveKey(operatorSecret, nodeName);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(operatorSecret);
        }
    }

    public ReadOnlyMemory<byte> Key
    {
        get
        {
            ObjectDisposedException.ThrowIf(_key is null, this);
            return _key;
        }
    }

    public void Dispose()
    {
        if (_key is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    private static byte[] DeriveKey(byte[] operatorSecret, string nodeName)
    {
        var derivedKey = new byte[ExpectedKeyLength];
        var info = Encoding.UTF8.GetBytes($"c0re-node-sqlite|v1|{nodeName}");

        HKDF.DeriveKey(HashAlgorithmName.SHA256, operatorSecret, derivedKey, EmptySalt, info);

        return derivedKey;
    }
}
