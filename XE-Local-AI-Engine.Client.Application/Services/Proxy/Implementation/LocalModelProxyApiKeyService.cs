namespace XE_Local_AI_Engine.Client.Services.Proxy.Implementation;

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Default <see cref="ILocalModelProxyApiKeyService" />. Mints 256-bit keys, persists only their SHA-256 digest
///     through <see cref="ILocalModelProxyApiKeyStore" />, and compares a presented key against that digest in constant
///     time. The plaintext never leaves <see cref="GenerateAsync" />.
/// </summary>
internal sealed class LocalModelProxyApiKeyService : ILocalModelProxyApiKeyService
{
    /// <summary>
    ///     Scheme marker. Makes a leaked key greppable and instantly attributable to this product's model proxy, lets
    ///     secret scanners recognise it, and distinguishes it from both the node's JWTs and the MCP key (<c>xemcp_</c>)
    ///     on the same origin.
    /// </summary>
    private const string KeyScheme = "xeprx_";

    /// <summary>256 bits — the same strength as the node's other generated secrets, and well past brute force over loopback.</summary>
    private const int KeyByteLength = 32;

    /// <summary>Characters of the secret retained in the non-secret display prefix. Enough to tell two keys apart, far too few to guess one.</summary>
    private const int PrefixSecretCharacters = 6;

    private readonly ILocalModelProxyApiKeyStore _store;
    private readonly TimeProvider _timeProvider;

    public LocalModelProxyApiKeyService(ILocalModelProxyApiKeyStore store, TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<GeneratedLocalModelProxyApiKey> GenerateAsync(CancellationToken cancellationToken = default)
    {
        // Base64Url: no padding and no '+', '/' or '=', so the key survives a shell argument, a JSON config file and an
        // HTTP header untouched — all three are on the path between this node and an external tool.
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(KeyByteLength));
        var key = KeyScheme + secret;
        var prefix = KeyScheme + secret[..PrefixSecretCharacters];

        var record = await _store.SetAsync(prefix, HashKey(key), cancellationToken).ConfigureAwait(false);

        // The only moment the plaintext exists outside the caller. Nothing downstream can reproduce it.
        return new GeneratedLocalModelProxyApiKey(key, ToView(record));
    }

    public async Task<LocalModelProxyApiKeyView?> GetAsync(CancellationToken cancellationToken = default)
    {
        var record = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToView(record);
    }

    public Task<bool> RevokeAsync(CancellationToken cancellationToken = default)
    {
        return _store.DeleteAsync(cancellationToken);
    }

    public async Task<bool> ValidateAsync(string? presented, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var record = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            // No key generated => the proxy authenticates nobody. Fail closed: an ungenerated credential must never
            // read as "no authentication required".
            return false;
        }

        // Hash the candidate and compare DIGESTS, never the plaintext — the plaintext is not recoverable here, which is
        // the whole point of storing a digest. FixedTimeEquals rather than SequenceEqual: a short-circuiting comparison
        // leaks the length of the matching prefix, which over a loopback socket is a practical byte-at-a-time oracle.
        // Two fixed-length SHA-256 digests also make the comparison naturally length-invariant, so a truncated
        // candidate is rejected on content rather than on an early length check.
        var candidate = HashKey(presented);
        var matches = CryptographicOperations.FixedTimeEquals(record.KeyHash.Span, candidate);

        CryptographicOperations.ZeroMemory(candidate);

        if (!matches)
        {
            return false;
        }

        await _store.TouchLastUsedAsync(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     A single SHA-256 over the key's UTF-8 bytes — deliberately NOT a password KDF. PBKDF2/Argon2/bcrypt exist to
    ///     make guessing a low-entropy human-chosen password expensive; the input here is 256 bits of CSPRNG output, so
    ///     there is no guess space to slow down and the only thing a KDF would buy is latency on every authenticated
    ///     proxy request. Unsalted for the same reason: a salt defeats precomputation across many weak secrets, and this
    ///     node has exactly one strong one. This is the standard construction for high-entropy API tokens.
    /// </summary>
    private static byte[] HashKey(string key)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }

    private static LocalModelProxyApiKeyView ToView(LocalModelProxyApiKeyRecord record)
    {
        return new LocalModelProxyApiKeyView(record.Prefix,
            DateTimeOffset.FromUnixTimeMilliseconds(record.CreatedAtUtc),
            record.LastUsedAtUtc is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(record.LastUsedAtUtc.Value));
    }
}
