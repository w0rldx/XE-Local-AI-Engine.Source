namespace XE_Local_AI_Engine.Tests.Mcp;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The inbound-MCP credential lifecycle: generation, reversible retrieval, revocation, and the fail-closed
///     validation the authentication handler depends on.
/// </summary>
public sealed class McpServerApiKeyServiceTests
{
    [Test]
    public async Task GenerateAsync_ProducesASchemePrefixedKeyAndAMatchingDisplayPrefix()
    {
        var service = CreateService(out _);

        var view = await service.GenerateAsync().ConfigureAwait(false);

        AssertEx.True(view.Key.StartsWith("xemcp_", StringComparison.Ordinal), "The key must carry the scheme marker so a leaked value is attributable.");
        AssertEx.True(view.Key.Length > 40, "A 256-bit base64url secret must be substantially longer than its prefix.");
        AssertEx.True(view.Key.StartsWith(view.Prefix, StringComparison.Ordinal), "The display prefix must be a genuine prefix of the key.");
        AssertEx.True(view.Prefix.Length < view.Key.Length, "The display prefix must not be the whole key.");
        AssertEx.Null(view.LastUsedAt);
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
    public async Task GetAsync_WhenNoKeyGenerated_ReturnsNull()
    {
        var service = CreateService(out _);

        AssertEx.Null(await service.GetAsync().ConfigureAwait(false));
    }

    [Test]
    public async Task GetAsync_ReturnsThePlaintextKey_SoTheOperatorCanReCopyIt()
    {
        var service = CreateService(out _);
        var generated = await service.GenerateAsync().ConfigureAwait(false);

        var fetched = AssertEx.NotNull(await service.GetAsync().ConfigureAwait(false));

        AssertEx.Equal(generated.Key, fetched.Key);
    }

    [Test]
    public async Task ValidateAsync_WhenNoKeyGenerated_FailsClosed()
    {
        var service = CreateService(out _);

        AssertEx.False(await service.ValidateAsync("xemcp_anything").ConfigureAwait(false),
            "A node with no generated key must authenticate nobody — an absent credential is not an open door.");
    }

    [Test]
    public async Task ValidateAsync_WithTheCorrectKey_Succeeds()
    {
        var service = CreateService(out _);
        var view = await service.GenerateAsync().ConfigureAwait(false);

        AssertEx.True(await service.ValidateAsync(view.Key).ConfigureAwait(false));
    }

    [Test]
    public async Task ValidateAsync_WithAWrongKey_Fails()
    {
        var service = CreateService(out _);
        _ = await service.GenerateAsync().ConfigureAwait(false);

        AssertEx.False(await service.ValidateAsync("xemcp_not-the-right-key").ConfigureAwait(false));
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
        // Guards the comparison against accepting a truncated candidate, which is the failure mode a naive
        // StartsWith/prefix comparison would introduce.
        var service = CreateService(out _);
        var view = await service.GenerateAsync().ConfigureAwait(false);

        AssertEx.False(await service.ValidateAsync(view.Key[..^1]).ConfigureAwait(false));
        AssertEx.False(await service.ValidateAsync(view.Prefix).ConfigureAwait(false));
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
        var view = await service.GenerateAsync().ConfigureAwait(false);

        AssertEx.True(await service.RevokeAsync().ConfigureAwait(false));

        AssertEx.Null(await service.GetAsync().ConfigureAwait(false));
        AssertEx.False(await service.ValidateAsync(view.Key).ConfigureAwait(false), "A revoked key must no longer authenticate.");
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
        var view = await service.GenerateAsync().ConfigureAwait(false);
        AssertEx.Null(view.LastUsedAt);

        _ = await service.ValidateAsync(view.Key).ConfigureAwait(false);

        AssertEx.True(AssertEx.NotNull(await store.GetAsync().ConfigureAwait(false)).LastUsedAtUtc.HasValue,
            "A successful authentication must stamp last-used.");
    }

    [Test]
    public async Task ValidateAsync_OnFailure_DoesNotStampLastUsed()
    {
        var service = CreateService(out var store);
        _ = await service.GenerateAsync().ConfigureAwait(false);

        _ = await service.ValidateAsync("xemcp_wrong").ConfigureAwait(false);

        AssertEx.False(AssertEx.NotNull(await store.GetAsync().ConfigureAwait(false)).LastUsedAtUtc.HasValue,
            "A rejected credential must not stamp last-used.");
    }

    private static IMcpServerApiKeyService CreateService(out InMemoryMcpServerApiKeyStore store)
    {
        store = new InMemoryMcpServerApiKeyStore();
        return new McpServerApiKeyService(store, new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1)));
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
    private sealed class InMemoryMcpServerApiKeyStore : IMcpServerApiKeyStore
    {
        private McpServerApiKeyRecord? _record;

        public Task<McpServerApiKeyRecord?> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_record);
        }

        public Task<McpServerApiKeyRecord> SetAsync(string prefix, string material, CancellationToken cancellationToken = default)
        {
            _record = new McpServerApiKeyRecord(prefix, material, CreatedAtUtc: 1, LastUsedAtUtc: null);
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
                _record = _record with { LastUsedAtUtc = timestampUtc };
            }

            return Task.CompletedTask;
        }
    }
}
