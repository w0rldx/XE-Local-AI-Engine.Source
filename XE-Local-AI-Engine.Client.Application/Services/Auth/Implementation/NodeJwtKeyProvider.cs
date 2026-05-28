namespace XE_Local_AI_Engine.Client.Services.Auth.Implementation;

using XE_Local_AI_Engine.Client.Services.Auth;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Persistence;

public sealed class NodeJwtKeyProvider : INodeJwtKeyProvider
{
    private const int SigningKeyLength = 32;
    private static readonly byte[] EmptySalt = [];

    private byte[]? _signingKey;

    public NodeJwtKeyProvider(IOptions<WorkerNodeOptions> workerNodeOptions, INodeOperatorSecretProvider operatorSecretProvider)
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
            _signingKey = DeriveSigningKey(operatorSecret, nodeName);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(operatorSecret);
        }
    }

    public ReadOnlyMemory<byte> SigningKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_signingKey is null, this);
            return _signingKey;
        }
    }

    public void Dispose()
    {
        if (_signingKey is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_signingKey);
        _signingKey = null;
    }

    private static byte[] DeriveSigningKey(byte[] operatorSecret, string nodeName)
    {
        var signingKey = new byte[SigningKeyLength];
        var info = Encoding.UTF8.GetBytes($"c0re-node-jwt|v1|{nodeName}");

        HKDF.DeriveKey(HashAlgorithmName.SHA256, operatorSecret, signingKey, EmptySalt, info);

        return signingKey;
    }
}
