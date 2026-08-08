namespace XE_Local_AI_Engine.Client.Security.DataProtection;

/// <summary>
///     Holds the 32-byte key-encryption-key (KEK) used to wrap the ASP.NET Core Data Protection key-ring at rest on
///     non-Windows hosts (BE-02). The KEK is derived from the node operator secret via HKDF-SHA256 with a Data
///     Protection-specific info string, so it is distinct from — but shares the same operator-secret root as — the
///     SQLite database key (<c>INodeSqliteKeyHolder</c>) and the JWT signing key (<c>INodeJwtKeyProvider</c>).
/// </summary>
/// <remarks>
///     Shape mirrors <c>INodeSqliteKeyHolder</c>: the holder owns the key material for the process lifetime and zeroes
///     it on dispose. The Windows build never registers this — it wraps the key-ring with DPAPI instead.
/// </remarks>
public interface INodeDataProtectionKeyProvider : IDisposable
{
    /// <summary>The 32-byte KEK used to AES-256-GCM wrap newly written key-ring elements.</summary>
    ReadOnlyMemory<byte> Key { get; }
}
