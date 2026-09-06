namespace XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Default <see cref="IIntegrationApiKeyService" />. Mints 256-bit keys, persists only their SHA-256 digest, and
///     compares a presented key against that digest in constant time. Copied from
///     <c>McpServerApiKeyService</c>, with the differences the many-keys-per-node shape forces: the row is resolved by
///     its PLAINTEXT display prefix (the digest column is encrypted at rest and cannot be queried), revocation is soft,
///     and every key belongs to a principal.
/// </summary>
internal sealed class IntegrationApiKeyService : IIntegrationApiKeyService
{
    /// <summary>Scheme marker. Makes a leaked key greppable, attributable to this product and recognisable to secret scanners.</summary>
    public const string KeyScheme = "xeint_";

    /// <summary>Characters of the secret retained in the non-secret display prefix. Enough to tell two keys apart, far too few to guess one.</summary>
    public const int PrefixSecretCharacters = 8;

    /// <summary>256 bits — the same strength as the node's other generated secrets, and well past brute force over loopback.</summary>
    private const int KeyByteLength = 32;

    /// <summary>The full length of a display prefix: the scheme marker plus <see cref="PrefixSecretCharacters" /> secret characters.</summary>
    public static readonly int PrefixLength = KeyScheme.Length + PrefixSecretCharacters;

    private readonly IIntegrationApiKeyStore _store;
    private readonly TimeProvider _timeProvider;

    public IntegrationApiKeyService(IIntegrationApiKeyStore store, TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<GeneratedIntegrationApiKey> GenerateAsync(string label,
        IReadOnlyList<Guid>? allowedTriggerIds,
        Guid? principalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        // Base64Url: no padding and no '+', '/' or '=', so the key survives a shell argument, a JSON config file and an
        // HTTP header untouched — all three sit between this node and the integrator that presents it.
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(KeyByteLength));
        var key = KeyScheme + secret;
        var prefix = KeyScheme + secret[..PrefixSecretCharacters];

        // A supplied principal is a ROTATION: the new credential inherits every session and in-flight execution the
        // old one owned. It is deliberately not validated against an existing row — a principal is an opaque grouping
        // id, not an entity with a lifecycle, so a "principal not found" check would be a second table for no
        // behaviour.
        var snapshot = await _store.CreateAsync(new IntegrationApiKeyCreateCommand(Guid.NewGuid(),
                                           principalId ?? Guid.NewGuid(),
                                           prefix,
                                           HashKey(key),
                                           label.Trim(),
                                           SerializeAllowList(allowedTriggerIds)),
                                       cancellationToken)
                                   .ConfigureAwait(false);

        // The only moment the plaintext exists outside the caller. Nothing downstream can reproduce it.
        return new GeneratedIntegrationApiKey(key, ToView(snapshot));
    }

    public async Task<IReadOnlyList<IntegrationApiKeyView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return snapshots.Select(ToView).ToArray();
    }

    public Task<bool> RevokeAsync(Guid keyId, CancellationToken cancellationToken = default) =>
        _store.RevokeAsync(keyId, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);

    public async Task<IntegrationApiKeyValidation?> ValidateAsync(string? presented, CancellationToken cancellationToken = default)
    {
        // Guard BEFORE slicing. The authentication handler calls this with whatever followed "Bearer " and does not
        // wrap it in a try/catch, so an unguarded `presented[..PrefixLength]` turns `Authorization: Bearer x` into a
        // 500 where a 401 is required — reachable by anyone who can reach the route.
        if (string.IsNullOrEmpty(presented)
            || !presented.StartsWith(KeyScheme, StringComparison.Ordinal)
            || presented.Length < PrefixLength)
        {
            return null;
        }

        var snapshot = await _store.GetByPrefixAsync(presented[..PrefixLength], cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.RevokedAtUtc is not null)
        {
            // Uniform: "no such prefix" and "revoked" read exactly like "wrong key" to the caller (ruling R2-6). The
            // lookup itself is not constant-time across prefixes — an unknown prefix skips the store read and the
            // hash — which is acceptable because the prefix is a public display value carrying no authority, and the
            // surface is loopback-only and rate-limited.
            return null;
        }

        // Compare DIGESTS in constant time. A short-circuiting comparison leaks the length of the matching prefix,
        // which over a loopback socket is a practical byte-at-a-time oracle.
        var candidate = HashKey(presented);
        var matches = CryptographicOperations.FixedTimeEquals(snapshot.KeyHash.Span, candidate);
        CryptographicOperations.ZeroMemory(candidate);

        if (!matches)
        {
            return null;
        }

        _ = await _store.TouchLastUsedAsync(snapshot.Id, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);

        return new IntegrationApiKeyValidation(snapshot.PrincipalId, snapshot.KeyPrefix, DeserializeAllowList(snapshot.AllowedTriggerIdsJson));
    }

    /// <summary>
    ///     Reads the stored allowlist. <see langword="null" /> — the column, or an unreadable value — means "every
    ///     trigger"; an empty list means "no trigger", which is a usable, if pointless, credential.
    /// </summary>
    public static IReadOnlyList<Guid>? DeserializeAllowList(string? allowedTriggerIdsJson)
    {
        if (string.IsNullOrWhiteSpace(allowedTriggerIdsJson))
        {
            return null;
        }

        try
        {
            // A literal JSON `null` deserialises to null, which would widen the key to EVERY trigger. Only a SQL NULL
            // column means "all triggers", and that is the early return above.
            return JsonSerializer.Deserialize<Guid[]>(allowedTriggerIdsJson) ?? [];
        }
        catch (JsonException)
        {
            // A column this node wrote cannot normally fail to parse. Failing CLOSED here would widen the key to every
            // trigger, so the safer reading is the narrow one: treat it as unreadable and refuse every trigger.
            return [];
        }
    }

    private static string? SerializeAllowList(IReadOnlyList<Guid>? allowedTriggerIds) =>
        allowedTriggerIds is null ? null : JsonSerializer.Serialize(allowedTriggerIds.Distinct().ToArray());

    /// <summary>
    ///     A single SHA-256 over the key's UTF-8 bytes — deliberately NOT a password KDF. The input is 256 bits of
    ///     CSPRNG output, so there is no guess space to slow down and a KDF would only add latency to every
    ///     authenticated request. Unsalted for the same reason.
    /// </summary>
    private static byte[] HashKey(string key) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private static IntegrationApiKeyView ToView(IntegrationApiKeySnapshot snapshot) =>
        new(snapshot.Id,
            snapshot.PrincipalId,
            snapshot.KeyPrefix,
            snapshot.Label,
            DeserializeAllowList(snapshot.AllowedTriggerIdsJson),
            DateTimeOffset.FromUnixTimeMilliseconds(snapshot.CreatedAtUtc),
            snapshot.LastUsedAtUtc is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(snapshot.LastUsedAtUtc.Value),
            snapshot.RevokedAtUtc is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(snapshot.RevokedAtUtc.Value));
}
