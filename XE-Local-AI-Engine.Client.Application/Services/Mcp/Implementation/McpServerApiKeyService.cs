namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Default <see cref="IMcpServerApiKeyService" />. Mints 256-bit keys, compares them in constant time, and delegates
///     storage to <see cref="IMcpServerApiKeyStore" /> (which encrypts the material at rest).
/// </summary>
internal sealed class McpServerApiKeyService : IMcpServerApiKeyService
{
    /// <summary>
    ///     Scheme marker. Makes a leaked key greppable and instantly attributable to this product, and lets secret
    ///     scanners recognise it. Also what distinguishes the token from the node's JWTs on the same origin.
    /// </summary>
    private const string KeyScheme = "xemcp_";

    /// <summary>256 bits — the same strength as the node's other generated secrets, and well past brute force over loopback.</summary>
    private const int KeyByteLength = 32;

    /// <summary>Characters of the secret retained in the non-secret display prefix. Enough to tell two keys apart, far too few to guess one.</summary>
    private const int PrefixSecretCharacters = 6;

    private readonly IMcpServerApiKeyStore _store;
    private readonly TimeProvider _timeProvider;

    public McpServerApiKeyService(IMcpServerApiKeyStore store, TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<McpServerApiKeyView> GenerateAsync(CancellationToken cancellationToken = default)
    {
        // Base64Url: no padding and no '+', '/' or '=' , so the key survives a shell argument, a JSON config file and an
        // HTTP header untouched — all three are on the path between this node and an external MCP client.
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(KeyByteLength));
        var key = KeyScheme + secret;
        var prefix = KeyScheme + secret[..PrefixSecretCharacters];

        var record = await _store.SetAsync(prefix, key, cancellationToken).ConfigureAwait(false);
        return ToView(record);
    }

    public async Task<McpServerApiKeyView?> GetAsync(CancellationToken cancellationToken = default)
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
            // No key generated => the endpoint authenticates nobody. Fail closed: an ungenerated credential must never
            // read as "no authentication required".
            return false;
        }

        // FixedTimeEquals over the UTF-8 bytes rather than string equality: a short-circuiting comparison leaks the
        // length of the matching prefix, which over a loopback socket is a practical byte-at-a-time oracle.
        var expected = Encoding.UTF8.GetBytes(record.Material);
        var actual = Encoding.UTF8.GetBytes(presented);
        var matches = CryptographicOperations.FixedTimeEquals(expected, actual);

        CryptographicOperations.ZeroMemory(expected);
        CryptographicOperations.ZeroMemory(actual);

        if (!matches)
        {
            return false;
        }

        await _store.TouchLastUsedAsync(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static McpServerApiKeyView ToView(McpServerApiKeyRecord record)
    {
        return new McpServerApiKeyView(record.Prefix,
            record.Material,
            DateTimeOffset.FromUnixTimeMilliseconds(record.CreatedAtUtc),
            record.LastUsedAtUtc is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(record.LastUsedAtUtc.Value));
    }
}
