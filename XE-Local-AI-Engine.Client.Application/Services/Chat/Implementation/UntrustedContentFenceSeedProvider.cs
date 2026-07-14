namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Default <see cref="IUntrustedContentFenceSeedProvider" />. Derives the fence seed with HKDF-SHA256 from the node
///     key (the same key-holder infrastructure the encrypted stores use) — never the RAW key, and never the public
///     conversation id alone. The node key is the HKDF input keying material, the conversation id is the salt (so the
///     seed is stable per conversation and distinct across conversations), and a constant purpose label is the HKDF
///     <c>info</c> (domain-separating this use from every other derivation of the same key). The 32-byte output is
///     hex-encoded so it drops straight into the string-seed framing API. A client that knows only the conversation id
///     cannot reproduce the seed without the node key, so it cannot forge the fence's closing marker.
/// </summary>
public sealed class UntrustedContentFenceSeedProvider(INodeSqliteKeyHolder keyHolder) : IUntrustedContentFenceSeedProvider
{
    private const int SeedByteLength = 32;

    // Domain-separation label: this HKDF info string binds the derived seed to THIS purpose so it can never collide with
    // another subkey derived from the same node key (e.g. the SQLite key, JWT signing key, or envelope wrap key).
    private static readonly byte[] PurposeInfo = Encoding.UTF8.GetBytes("xe:untrusted-attachment-fence-nonce|v1");

    private readonly INodeSqliteKeyHolder _keyHolder = keyHolder ?? throw new ArgumentNullException(nameof(keyHolder));

    public string DeriveSeed(Guid conversationId)
    {
        var salt = conversationId.ToByteArray();
        var derived = new byte[SeedByteLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _keyHolder.Key.Span, derived, salt, PurposeInfo);
        return Convert.ToHexStringLower(derived);
    }
}
