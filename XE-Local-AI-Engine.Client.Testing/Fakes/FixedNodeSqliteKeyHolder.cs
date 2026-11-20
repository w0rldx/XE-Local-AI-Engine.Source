namespace XE_Local_AI_Engine.Client.Testing.Fakes;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Test double for <see cref="INodeSqliteKeyHolder" />: holds caller-supplied key material, so a store under test
///     runs the real encrypted-column path. Takes ownership of the array and zeroes it on dispose.
/// </summary>
public sealed class FixedNodeSqliteKeyHolder : INodeSqliteKeyHolder
{
    private byte[]? _key;

    public FixedNodeSqliteKeyHolder(byte[] key)
    {
        _key = key;
    }

    public ReadOnlyMemory<byte> Key
    {
        get
        {
            ObjectDisposedException.ThrowIf(_key is null, this);
            return _key;
        }
    }

    /// <summary>A fresh deterministic 32-byte key: the bytes 1 through 32.</summary>
    public static byte[] CreateTestKey()
    {
        return Enumerable.Range(start: 0, count: 32).Select(static value => (byte)(value + 1)).ToArray();
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
}
