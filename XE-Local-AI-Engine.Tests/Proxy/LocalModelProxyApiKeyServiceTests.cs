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
[Category(TestCategories.Unit)]
public sealed class LocalModelProxyApiKeyServiceTests
{
    [Test]
    public async Task GenerateAsync_ProducesASchemePrefixedKeyAndAMatchingDisplayPrefix()
    {
        var service = CreateService(out _);

        var generated = await service.GenerateAsync();

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

        var first = await service.GenerateAsync();
        var second = await service.GenerateAsync();

        AssertEx.True(first.Key != second.Key, "Each generation must mint fresh key material.");
    }

    [Test]
    public async Task GenerateAsync_PersistsOnlyAOneWayDigest_SoAStoreReadYieldsNothingUsable()
    {
        var service = CreateService(out var store);

        var generated = await service.GenerateAsync();
        var stored = AssertEx.NotNull(await store.GetAsync());

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

        AssertEx.Null(await service.GetAsync());
    }

    [Test]
    public async Task GetAsync_ReturnsMetadataOnly_AndCannotRecoverTheKey()
    {
        var service = CreateService(out _);
        var generated = await service.GenerateAsync();

        var fetched = AssertEx.NotNull(await service.GetAsync());

        AssertEx.Equal(generated.View.Prefix, fetched.Prefix);
        AssertEx.False(await service.ValidateAsync(fetched.Prefix),
            "Everything still retrievable after generation must be useless as a credential.");
    }

    [Test]
    public async Task ValidateAsync_WhenNoKeyGenerated_FailsClosed()
    {
        var service = CreateService(out _);

        AssertEx.False(await service.ValidateAsync("xeprx_anything"),
            "A node with no generated key must authenticate nobody — an absent credential is not an open door.");
    }

    [Test]
    public async Task ValidateAsync_WithTheCorrectKey_Succeeds()
    {
        var service = CreateService(out _);
        var generated = await service.GenerateAsync();

        AssertEx.True(await service.ValidateAsync(generated.Key));
    }

    [Test]
    public async Task ValidateAsync_WithAWrongKey_Fails()
    {
        var service = CreateService(out _);
        _ = await service.GenerateAsync();

        AssertEx.False(await service.ValidateAsync("xeprx_not-the-right-key"));
    }

    [Test]
    public async Task ValidateAsync_WithNullOrEmpty_Fails()
    {
        var service = CreateService(out _);
        _ = await service.GenerateAsync();

        AssertEx.False(await service.ValidateAsync(presented: null));
        AssertEx.False(await service.ValidateAsync(string.Empty));
    }

    [Test]
    public async Task ValidateAsync_WithAProperPrefixOfTheKey_Fails()
    {
        var service = CreateService(out _);
        var generated = await service.GenerateAsync();

        AssertEx.False(await service.ValidateAsync(generated.Key[..^1]));
        AssertEx.False(await service.ValidateAsync(generated.View.Prefix));
    }

    [Test]
    public async Task GenerateAsync_ReplacesThePreviousKey_SoTheOldOneStopsAuthenticating()
    {
        var service = CreateService(out _);
        var original = await service.GenerateAsync();

        var replacement = await service.GenerateAsync();

        AssertEx.True(await service.ValidateAsync(replacement.Key), "The new key must authenticate.");
        AssertEx.False(await service.ValidateAsync(original.Key),
            "Regenerating must immediately invalidate the replaced key — there is no window in which both work.");
    }

    [Test]
    public async Task RevokeAsync_RemovesTheKeyAndClosesTheEndpoint()
    {
        var service = CreateService(out _);
        var generated = await service.GenerateAsync();

        AssertEx.True(await service.RevokeAsync());

        AssertEx.Null(await service.GetAsync());
        AssertEx.False(await service.ValidateAsync(generated.Key), "A revoked key must no longer authenticate.");
    }

    [Test]
    public async Task RevokeAsync_WhenNoKeyExists_ReturnsFalse()
    {
        var service = CreateService(out _);

        AssertEx.False(await service.RevokeAsync());
    }

    [Test]
    public async Task ValidateAsync_OnSuccess_StampsLastUsed()
    {
        var service = CreateService(out var store);
        var generated = await service.GenerateAsync();
        AssertEx.Null(generated.View.LastUsedAt);

        _ = await service.ValidateAsync(generated.Key);

        AssertEx.True(AssertEx.NotNull(await store.GetAsync()).LastUsedAtUtc.HasValue,
            "A successful authentication must stamp last-used.");
    }

    [Test]
    public async Task ValidateAsync_OnFailure_DoesNotStampLastUsed()
    {
        var service = CreateService(out var store);
        _ = await service.GenerateAsync();

        _ = await service.ValidateAsync("xeprx_wrong");

        AssertEx.False(AssertEx.NotNull(await store.GetAsync()).LastUsedAtUtc.HasValue,
            "A rejected credential must not stamp last-used.");
    }

    private static ILocalModelProxyApiKeyService CreateService(out InMemoryLocalModelProxyApiKeyStore store)
    {
        store = new InMemoryLocalModelProxyApiKeyStore();
        return new LocalModelProxyApiKeyService(store, new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1)));
    }

    /// <summary>Deterministic clock so the last-used assertions do not depend on wall time.</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
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
