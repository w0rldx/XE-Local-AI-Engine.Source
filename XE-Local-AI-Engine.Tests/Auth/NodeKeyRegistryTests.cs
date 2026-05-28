namespace XE_Local_AI_Engine.Tests.Auth;

using NSec.Cryptography;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeKeyRegistryTests
{
    [Test]
    public void Rotate_WhenKeyRotates_PreviousActiveKeyRemainsAvailableDuringGraceWindow()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero));
        using var registry = new NodeKeyRegistry(timeProvider);
        using var firstKey = Key.Create(KeyAgreementAlgorithm.X25519);
        using var secondKey = Key.Create(KeyAgreementAlgorithm.X25519);

        registry.Rotate("node-key-1", firstKey);
        registry.Rotate("node-key-2", secondKey);

        var active = registry.Resolve("node-key-2");
        var retired = registry.Resolve("node-key-1");

        AssertEx.Equal(NodeKeyLookupStatus.Active, active.Status);
        AssertEx.Equal("node-key-2", active.KeyIdUsed);
        AssertEx.Equal(secondKey, active.PrivateKey);
        AssertEx.True(active.IsResolved);

        AssertEx.Equal(NodeKeyLookupStatus.Retired, retired.Status);
        AssertEx.Equal("node-key-1", retired.KeyIdUsed);
        AssertEx.Equal(firstKey, retired.PrivateKey);
        AssertEx.True(retired.IsResolved);
    }

    [Test]
    public void Resolve_WhenRetiredKeyGraceWindowExpires_ReturnsRetiredExpired()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero));
        using var registry = new NodeKeyRegistry(timeProvider);
        using var firstKey = Key.Create(KeyAgreementAlgorithm.X25519);
        using var secondKey = Key.Create(KeyAgreementAlgorithm.X25519);

        registry.Rotate("node-key-1", firstKey);
        registry.Rotate("node-key-2", secondKey);
        timeProvider.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

        var expired = registry.Resolve("node-key-1");
        var missingAfterEviction = registry.Resolve("node-key-1");

        AssertEx.Equal(NodeKeyLookupStatus.RetiredExpired, expired.Status);
        AssertEx.Equal("node-key-1", expired.KeyIdUsed);
        AssertEx.False(expired.IsResolved);
        AssertEx.Equal(NodeKeyLookupStatus.Missing, missingAfterEviction.Status);
        AssertEx.Equal("node-key-2", missingAfterEviction.KeyIdUsed);
        AssertEx.False(missingAfterEviction.IsResolved);
    }

    [Test]
    public void Resolve_WhenNoMatchingKeyExists_ReturnsMissing()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero));
        using var registry = new NodeKeyRegistry(timeProvider);
        using var activeKey = Key.Create(KeyAgreementAlgorithm.X25519);

        registry.Rotate("active-key", activeKey);

        var result = registry.Resolve("unknown-key");

        AssertEx.Equal(NodeKeyLookupStatus.Missing, result.Status);
        AssertEx.Equal("active-key", result.KeyIdUsed);
        AssertEx.False(result.IsResolved);
        AssertEx.Null(result.PrivateKey);
    }

    [Test]
    public void ResolveGraceEligible_WhenKeyRotates_ReturnsActiveThenRetiredKeys()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero));
        using var registry = new NodeKeyRegistry(timeProvider);
        using var firstKey = Key.Create(KeyAgreementAlgorithm.X25519);
        using var secondKey = Key.Create(KeyAgreementAlgorithm.X25519);

        registry.Rotate("node-key-1", firstKey);
        registry.Rotate("node-key-2", secondKey);

        var resolutions = registry.ResolveGraceEligible();

        AssertEx.Equal(2, resolutions.Count);
        AssertEx.Equal(NodeKeyLookupStatus.Active, resolutions[0].Status);
        AssertEx.Equal("node-key-2", resolutions[0].KeyIdUsed);
        AssertEx.Equal(NodeKeyLookupStatus.Retired, resolutions[1].Status);
        AssertEx.Equal("node-key-1", resolutions[1].KeyIdUsed);
    }

    [Test]
    public void ResolveGraceEligible_WhenRetiredGraceWindowExpires_ExcludesExpiredRetiredKeys()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero));
        using var registry = new NodeKeyRegistry(timeProvider);
        using var firstKey = Key.Create(KeyAgreementAlgorithm.X25519);
        using var secondKey = Key.Create(KeyAgreementAlgorithm.X25519);

        registry.Rotate("node-key-1", firstKey);
        registry.Rotate("node-key-2", secondKey);
        timeProvider.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

        var resolutions = registry.ResolveGraceEligible();

        AssertEx.Equal(1, resolutions.Count);
        AssertEx.Equal(NodeKeyLookupStatus.Active, resolutions[0].Status);
        AssertEx.Equal("node-key-2", resolutions[0].KeyIdUsed);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan delta)
        {
            _utcNow = _utcNow.Add(delta);
        }
    }
}
