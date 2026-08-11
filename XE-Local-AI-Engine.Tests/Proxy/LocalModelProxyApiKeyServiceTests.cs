namespace XE_Local_AI_Engine.Tests.Proxy;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Proxy;
using XE_Local_AI_Engine.Client.Services.Proxy.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The inbound model-proxy credential lifecycle: generation, ONE-WAY storage, revocation, and the fail-closed
///     validation the authentication handler depends on. The load-bearing property is that the key is shown exactly once
///     and is unrecoverable afterwards — a database read must yield nothing that can be presented to the proxy endpoint.
///     This mirrors the MCP key's guarantees because the proxy shares its security posture and differs only in scheme.
/// </summary>
public sealed class LocalModelProxyApiKeyServiceTests
{
    [Test]
    public async Task GenerateAsync_ProducesASchemePrefixedKeyAndAMatchingDisplayPrefix()
    {
        var service = CreateService(out _);

        var generated = await service.GenerateAsync().ConfigureAwait(false);

        AssertEx.True(generated.Key.StartsWith("xeprx_", StringComparison.Ordinal), "The key must carry the scheme marker so a leaked value is attributable.");
        AssertEx.True(generated.Key.Length > 40, "A 256-bit base64url secret must be substantially longer than its prefix.");
        AssertEx.True(generated.Key.StartsWith(generated.View.Prefix, StringComparison.Ordinal), "The display prefix must be a genuine prefix of the key.");
        AssertEx.True(generated.View.Prefix.Length < generated.Key.Length, "The display prefix must not be the whole key.");
        AssertEx.Null(generated.View.LastUsedAt);
    }

    [Test]
    public async Task GenerateAsync_TwiceProducesDifferentKeys()
    {
        var service = CreateService(out _);

        var first = await service.GenerateAsync().ConfigureAwait(false);
        var second = await service.GenerateAsync().ConfigureAwait(false);

        AssertEx.True(first.Key != second.Key, "Each generation must mint fresh key material.");
    }

    [Test]
    public async Task GenerateAsync_PersistsOnlyAOneWayDigest_SoAStoreReadYieldsNothingUsable()
    {
        var service = CreateService(out var store);

        var generated = await service.GenerateAsync().ConfigureAwait(false);
        var stored = AssertEx.NotNull(await store.GetAsync().ConfigureAwait(false));

        AssertEx.Equal(32, stored.KeyHash.Length);
        AssertEx.True(stored.KeyHash.Span.SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(generated.Key))),
            "The stored value must be the SHA-256 digest of the key.");
        AssertEx.False(stored.KeyHash.Span.SequenceEqual(Encoding.UTF8.GetBytes(generated.Key)),
            "The stored value must not be the key's own bytes — that would be reversible storage under a new name.");
    }

    [Test]
    public async Task GetAsync_WhenNoKeyGenerated_ReturnsNull()
    {
        var service = CreateService(out _);

        AssertEx.Null(await service.GetAsync().ConfigureAwait(false));
    }

    [Test]
    public async Task GetAsync_ReturnsMetadataOnly_AndCannotRecoverTheKey()
    {
        var service = CreateService(out _);
        var generated = await service.GenerateAsync().ConfigureAwait(false);

        var fetched = AssertEx.NotNull(await service.GetAsync().ConfigureAwait(false));

        AssertEx.Equal(generated.View.Prefix, fetched.Prefix);
        AssertEx.False(await service.ValidateAsync(fetched.Prefix).ConfigureAwait(false),
            "Everything still retrievable after generation must be useless as a credential.");
    }

    [Test]
    public async Task ValidateAsync_WhenNoKeyGenerated_FailsClosed()
    {
        var service = CreateService(out _);

        AssertEx.False(await service.ValidateAsync("xeprx_anything").ConfigureAwait(false),
            "A node with no generated key must authenticate nobody — an absent credential is not an open door.");
    }

    [Test]
    public async Task ValidateAsync_WithTheCorrectKey_Succeeds()
    {
        var service = CreateService(out _);
        var generated = await service.GenerateAsync().ConfigureAwait(false);

        AssertEx.True(await service.ValidateAsync(generated.Key).ConfigureAwait(false));
    }

    [Test]
    public async Task ValidateAsync_WithAWrongKey_Fails()
    {
        var service = CreateService(out _);
        _ = await service.GenerateAsync().ConfigureAwait(false);

        AssertEx.False(await service.ValidateAsync("xeprx_not-the-right-key").ConfigureAwait(false));
    }

    [Test]
    public async Task ValidateAsync_WithNullOrEmpty_Fails()
    {
        var service = CreateService(out _);
        _ = await service.GenerateAsync().ConfigureAwait(false);

        AssertEx.False(await service.ValidateAsync(presented: null).ConfigureAwait(false));
        AssertEx.False(await service.ValidateAsync(string.Empty).ConfigureAwait(false));
    }

    [Test]
    public async Task ValidateAsync_WithAProperPrefixOfTheKey_Fails()
    {
        var service = CreateService(out _);
        var generated = await service.GenerateAsync().ConfigureAwait(false);

        AssertEx.False(await service.ValidateAsync(generated.Key[..^1]).ConfigureAwait(false));
        AssertEx.False(await service.ValidateAsync(generated.View.Prefix).ConfigureAwait(false));
    }

    [Test]
    public async Task GenerateAsync_ReplacesThePreviousKey_SoTheOldOneStopsAuthenticating()
    {
        var service = CreateService(out _);
        var original = await service.GenerateAsync().ConfigureAwait(false);

        var replacement = await service.GenerateAsync().ConfigureAwait(false);

        AssertEx.True(await service.ValidateAsync(replacement.Key).ConfigureAwait(false), "The new key must authenticate.");
        AssertEx.False(await service.ValidateAsync(original.Key).ConfigureAwait(false),
            "Regenerating must immediately invalidate the replaced key — there is no window in which both work.");
    }

    [Test]
    public async Task RevokeAsync_RemovesTheKeyAndClosesTheEndpoint()
    {
        var service = CreateService(out _);
        var generated = await service.GenerateAsync().ConfigureAwait(false);

        AssertEx.True(await service.RevokeAsync().ConfigureAwait(false));

        AssertEx.Null(await service.GetAsync().ConfigureAwait(false));
        AssertEx.False(await service.ValidateAsync(generated.Key).ConfigureAwait(false), "A revoked key must no longer authenticate.");
    }

    [Test]
    public async Task RevokeAsync_WhenNoKeyExists_ReturnsFalse()
    {
        var service = CreateService(out _);

        AssertEx.False(await service.RevokeAsync().ConfigureAwait(false));
    }

    [Test]
    public async Task ValidateAsync_OnSuccess_StampsLastUsed()
    {
        var service = CreateService(out var store);
        var generated = await service.GenerateAsync().ConfigureAwait(false);
        AssertEx.Null(generated.View.LastUsedAt);

        _ = await service.ValidateAsync(generated.Key).ConfigureAwait(false);

        AssertEx.True(AssertEx.NotNull(await store.GetAsync().ConfigureAwait(false)).LastUsedAtUtc.HasValue,
            "A successful authentication must stamp last-used.");
    }

    [Test]
    public async Task ValidateAsync_OnFailure_DoesNotStampLastUsed()
    {
        var service = CreateService(out var store);
        _ = await service.GenerateAsync().ConfigureAwait(false);

        _ = await service.ValidateAsync("xeprx_wrong").ConfigureAwait(false);

        AssertEx.False(AssertEx.NotNull(await store.GetAsync().ConfigureAwait(false)).LastUsedAtUtc.HasValue,
            "A rejected credential must not stamp last-used.");
    }

    private static ILocalModelProxyApiKeyService CreateService(out InMemoryLocalModelProxyApiKeyStore store)
    {
        store = new InMemoryLocalModelProxyApiKeyStore();
        return new LocalModelProxyApiKeyService(store, new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1)));
    }

    /// <summary>Deterministic clock so the last-used assertions do not depend on wall time.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    /// <summary>
    ///     In-memory stand-in for the EF store. Mirrors the real singleton-upsert semantics (a set REPLACES, and resets
    ///     the timestamps) so the replacement/revocation behaviour under test is the behaviour that actually ships.
    /// </summary>
    private sealed class InMemoryLocalModelProxyApiKeyStore : ILocalModelProxyApiKeyStore
    {
        private LocalModelProxyApiKeyRecord? _record;

        public Task<LocalModelProxyApiKeyRecord?> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_record);
        }

        public Task<LocalModelProxyApiKeyRecord> SetAsync(string prefix, ReadOnlyMemory<byte> keyHash, CancellationToken cancellationToken = default)
        {
            _record = new LocalModelProxyApiKeyRecord(prefix, keyHash, CreatedAtUtc: 1, LastUsedAtUtc: null);
            return Task.FromResult(_record);
        }

        public Task<bool> DeleteAsync(CancellationToken cancellationToken = default)
        {
            var existed = _record is not null;
            _record = null;
            return Task.FromResult(existed);
        }

        public Task TouchLastUsedAsync(long timestampUtc, CancellationToken cancellationToken = default)
        {
            if (_record is not null)
            {
                _record = _record with
                {
                    LastUsedAtUtc = timestampUtc
                };
            }

            return Task.CompletedTask;
        }
    }
}
