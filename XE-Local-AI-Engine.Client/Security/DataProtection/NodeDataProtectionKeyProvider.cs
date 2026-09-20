namespace XE_Local_AI_Engine.Client.Security.DataProtection;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Persistence;

/// <summary>
///     Derives and holds the Data Protection key-ring KEK.
/// </summary>
/// <remarks>
///     Mirrors <c>NodeSqliteKeyHolder</c> and <c>NodeJwtKeyProvider</c> exactly — same HKDF-SHA256 derivation, same
///     operator-secret source, same zero-on-dispose discipline — but with a Data Protection-specific info string so
///     the three keys never collide. The at-rest (<c>c0re-node-sqlite</c>), auth (<c>c0re-node-jwt</c>) and key-ring
///     (<c>c0re-node-dpkeyring</c>) derivations MUST keep distinct info strings; collapsing them would let one key
///     material stand in for another.
/// </remarks>
public sealed class NodeDataProtectionKeyProvider : INodeDataProtectionKeyProvider
{
    private const int ExpectedKeyLength = 32;
    private static readonly byte[] EmptySalt = [];

    private byte[]? _key;

    public NodeDataProtectionKeyProvider(IOptions<WorkerNodeOptions> workerNodeOptions, INodeOperatorSecretProvider operatorSecretProvider)
    {
        ArgumentNullException.ThrowIfNull(workerNodeOptions);
        ArgumentNullException.ThrowIfNull(operatorSecretProvider);

        var nodeName = workerNodeOptions.Value.NodeName;
        if (string.IsNullOrWhiteSpace(nodeName))
        {
            throw new InvalidOperationException("WorkerNode:NodeName must be configured.");
        }

        // Fail-closed: a MISSING operator secret throws here, because the key-ring cannot be wrapped or unwrapped without it. A WRONG one need not fail startup —
        // the node store is PLAIN SQLite with application-level COLUMN encryption, so a column fails only when read, and NodeDataProtectionKeyRingFailClosedKeyResolver is the loud backstop.
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
        var info = Encoding.UTF8.GetBytes($"c0re-node-dpkeyring|v1|{nodeName}");

        HKDF.DeriveKey(HashAlgorithmName.SHA256, operatorSecret, derivedKey, EmptySalt, info);

        return derivedKey;
    }
}
