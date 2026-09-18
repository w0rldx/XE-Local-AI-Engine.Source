namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The inbound-MCP credential lifecycle: generation, ONE-WAY storage, revocation, and the fail-closed validation
///     the authentication handler depends on. The load-bearing property is that the key is shown exactly once and is
///     unrecoverable afterwards — a database read must yield nothing that can be presented to the MCP endpoint.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class McpServerApiKeyServiceTests
{
    [Test]
    public async Task GenerateAsync_ProducesASchemePrefixedKeyAndAMatchingDisplayPrefix()
    {
        var service = CreateService(out _);

        var generated = await service.GenerateAsync();

        AssertEx.True(generated.Key.StartsWith("xemcp_", StringComparison.Ordinal), "The key must carry the scheme marker so a leaked value is attributable.");
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
    public async Task GenerateAsync_AgenticScopePersistsAndReturnsAgenticMetadata()
    {
        var service = CreateService(out var store);

        var generated = await service.GenerateAsync(McpServerApiKeyScope.Agentic);

        AssertEx.Equal(McpServerApiKeyScope.Agentic, generated.View.Scope);
        AssertEx.Equal(McpServerApiKeyScope.Agentic, AssertEx.NotNull(await service.ValidateAsync(generated.Key)).Scope);
        AssertEx.Equal((int)McpServerApiKeyScope.Agentic, AssertEx.NotNull(await store.GetAsync()).Scope);
    }

    [Test]
    public async Task GenerateAsync_WithUndefinedScope_IsRejectedBeforePersistence()
    {
        var service = CreateService(out var store);

        _ = await AssertEx.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.GenerateAsync((McpServerApiKeyScope)42));

        AssertEx.Null(await store.GetAsync());
    }

    [Test]
    public async Task GenerateAsync_PersistsOnlyAOneWayDigest_SoAStoreReadYieldsNothingUsable()
    {
        // The core guarantee of hashed storage: everything the node retains is derivable FROM the key, and nothing
        // retained can reproduce it. This is what makes a database backup, a sync folder or a forensic image inert.
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
        // The inverse of the retrieval contract this surface used to have. The key is shown exactly once, at
        // generation; afterwards only non-secret metadata is retrievable and a lost key can only be replaced.
        // `McpServerApiKeyView` having no key member is the primary enforcement — this pins the behaviour that
        // nothing retrievable can stand in for the key either.
        var service = CreateService(out _);
        var generated = await service.GenerateAsync();

        var fetched = AssertEx.NotNull(await service.GetAsync());

        AssertEx.Equal(generated.View.Prefix, fetched.Prefix);
        AssertEx.Null(await service.ValidateAsync(fetched.Prefix),
            "Everything still retrievable after generation must be useless as a credential.");
    }

    [Test]
    public async Task ValidateAsync_WhenNoKeyGenerated_FailsClosed()
    {
        var service = CreateService(out _);

        AssertEx.Null(await service.ValidateAsync("xemcp_anything"),
            "A node with no generated key must authenticate nobody — an absent credential is not an open door.");
    }

    [Test]
    public async Task ValidateAsync_WithTheCorrectKey_Succeeds()
    {
        var service = CreateService(out _);
        var generated = await service.GenerateAsync();

        var validation = AssertEx.NotNull(await service.ValidateAsync(generated.Key));
        AssertEx.Equal(McpServerApiKeyScope.Delegate, validation.Scope);
        AssertEx.Equal(generated.View.Prefix, validation.Prefix);
    }

    [Test]
    public async Task ValidateAsync_WithAWrongKey_Fails()
    {
        var service = CreateService(out _);
        _ = await service.GenerateAsync();

        AssertEx.Null(await service.ValidateAsync("xemcp_not-the-right-key"));
    }

    [Test]
    public async Task ValidateAsync_WithNullOrEmpty_Fails()
    {
        var service = CreateService(out _);
        _ = await service.GenerateAsync();

        AssertEx.Null(await service.ValidateAsync(presented: null));
        AssertEx.Null(await service.ValidateAsync(string.Empty));
    }

    [Test]
    public async Task ValidateAsync_WithAProperPrefixOfTheKey_Fails()
    {
        // Guards the comparison against accepting a truncated candidate, which is the failure mode a naive
        // StartsWith/prefix comparison would introduce.
        var service = CreateService(out _);
        var generated = await service.GenerateAsync();

        AssertEx.Null(await service.ValidateAsync(generated.Key[..^1]));
        AssertEx.Null(await service.ValidateAsync(generated.View.Prefix));
    }

    [Test]
    public async Task GenerateAsync_ReplacesThePreviousKey_SoTheOldOneStopsAuthenticating()
    {
        var service = CreateService(out _);
        var original = await service.GenerateAsync();

        var replacement = await service.GenerateAsync();

        AssertEx.NotNull(await service.ValidateAsync(replacement.Key));
        AssertEx.Null(await service.ValidateAsync(original.Key),
            "Regenerating must immediately invalidate the replaced key — there is no window in which both work.");
    }

    [Test]
    public async Task GenerateAsync_RotatesSecretAndScopeAsOneSingletonReplacement()
    {
        var service = CreateService(out _);
        var original = await service.GenerateAsync(McpServerApiKeyScope.Agentic);

        var replacement = await service.GenerateAsync(McpServerApiKeyScope.Delegate);

        AssertEx.Null(await service.ValidateAsync(original.Key),
            "Changing scope must invalidate the prior secret with no dual-valid window.");
        AssertEx.Equal(McpServerApiKeyScope.Delegate,
            AssertEx.NotNull(await service.ValidateAsync(replacement.Key)).Scope);
    }

    [Test]
    public async Task RevokeAsync_RemovesTheKeyAndClosesTheEndpoint()
    {
        var service = CreateService(out _);
        var generated = await service.GenerateAsync();

        AssertEx.True(await service.RevokeAsync());

        AssertEx.Null(await service.GetAsync());
        AssertEx.Null(await service.ValidateAsync(generated.Key), "A revoked key must no longer authenticate.");
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

        _ = await service.ValidateAsync("xemcp_wrong");

        AssertEx.False(AssertEx.NotNull(await store.GetAsync()).LastUsedAtUtc.HasValue,
            "A rejected credential must not stamp last-used.");
    }

    [Test]
    public async Task ValidateAsync_WhenRotationWinsBetweenReadAndTouch_RejectsOldKeyWithoutStampingReplacement()
    {
        var service = CreateService(out var store);
        var original = await service.GenerateAsync(McpServerApiKeyScope.Agentic);
        store.RotateImmediatelyBeforeNextTouch();

        var validation = await service.ValidateAsync(original.Key);

        AssertEx.Null(validation, "A key rotated after validation's read must not authenticate from its stale snapshot.");
        var replacement = AssertEx.NotNull(await store.GetAsync());
        AssertEx.Equal((int)McpServerApiKeyScope.Delegate, replacement.Scope);
        AssertEx.Null(replacement.LastUsedAtUtc, "The stale authentication attempt must not stamp the replacement key.");
    }

    private static IMcpServerApiKeyService CreateService(out InMemoryMcpServerApiKeyStore store)
    {
        store = new InMemoryMcpServerApiKeyStore();
        return new McpServerApiKeyService(store, new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1)));
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
    private sealed class InMemoryMcpServerApiKeyStore : IMcpServerApiKeyStore
    {
        private McpServerApiKeyRecord? _record;
        private bool _rotateBeforeTouch;

        public void RotateImmediatelyBeforeNextTouch()
        {
            _rotateBeforeTouch = true;
        }

        public Task<McpServerApiKeyRecord?> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_record);
        }

        public Task<McpServerApiKeyRecord> SetAsync(string prefix,
            ReadOnlyMemory<byte> keyHash,
            int scope,
            CancellationToken cancellationToken = default)
        {
            _record = new McpServerApiKeyRecord(prefix, keyHash, scope, Guid.NewGuid(), CreatedAtUtc: 1, LastUsedAtUtc: null);
            return Task.FromResult(_record);
        }

        public Task<bool> DeleteAsync(CancellationToken cancellationToken = default)
        {
            var existed = _record is not null;
            _record = null;
            return Task.FromResult(existed);
        }

        public Task<bool> TouchLastUsedAsync(Guid generationId, long timestampUtc, CancellationToken cancellationToken = default)
        {
            if (_rotateBeforeTouch)
            {
                _rotateBeforeTouch = false;
                _record = new McpServerApiKeyRecord("xemcp_replacement",
                    SHA256.HashData(Encoding.UTF8.GetBytes("xemcp_replacement-key")),
                    (int)McpServerApiKeyScope.Delegate,
                    Guid.NewGuid(),
                    CreatedAtUtc: 2,
                    LastUsedAtUtc: null);
            }

            if (_record is null || _record.GenerationId != generationId)
            {
                return Task.FromResult(false);
            }

            _record = _record with
            {
                LastUsedAtUtc = timestampUtc
            };
            return Task.FromResult(true);
        }
    }
}
