namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The <c>xeint_</c> credential lifecycle. Two properties are load-bearing: the plaintext is shown exactly once and
///     is unrecoverable afterwards, and every rejection — malformed, unknown prefix, wrong digest, revoked — is the
///     same <see langword="null" />, because a caller must never learn which of the four it hit (ruling R2-6).
/// </summary>
public sealed class IntegrationApiKeyServiceTests
{
    [Test]
    public async Task GenerateAsync_ProducesASchemePrefixedKeyAndAMatchingFourteenCharacterDisplayPrefix()
    {
        var service = CreateService(out _);

        var generated = await service.GenerateAsync("ingest", allowedTriggerIds: null, principalId: null).ConfigureAwait(false);

        AssertEx.True(generated.Key.StartsWith("xeint_", StringComparison.Ordinal), "The key must carry the scheme marker so a leaked value is attributable.");
        AssertEx.True(generated.Key.StartsWith(generated.View.KeyPrefix, StringComparison.Ordinal), "The display prefix must be a genuine prefix of the key.");
        AssertEx.Equal(expected: 14, generated.View.KeyPrefix.Length, "The display prefix is the six-character scheme plus eight secret characters.");
        AssertEx.True(generated.Key.Length > generated.View.KeyPrefix.Length + 20, "A 256-bit base64url secret must be far longer than its display prefix.");
        AssertEx.Null(generated.View.LastUsedAt);
        AssertEx.Null(generated.View.RevokedAt);
    }

    [Test]
    public async Task GenerateAsync_StoresOnlyTheDigest_SoThePlaintextIsUnrecoverable()
    {
        var service = CreateService(out var store);

        var generated = await service.GenerateAsync("ingest", allowedTriggerIds: null, principalId: null).ConfigureAwait(false);

        var row = store.Rows.Single();
        AssertEx.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(generated.Key))), Convert.ToHexString(row.KeyHash.ToArray()));
        AssertEx.False(row.Label.Contains(generated.Key, StringComparison.Ordinal), "No stored column may carry the plaintext.");
        AssertEx.False(generated.Key.Equals(row.KeyPrefix, StringComparison.Ordinal));

        var listed = (await service.ListAsync().ConfigureAwait(false)).Single();
        AssertEx.Equal(generated.View.KeyPrefix, listed.KeyPrefix);
    }

    [Test]
    public async Task GenerateAsync_MintsManyKeysRatherThanReplacingTheSingleton()
    {
        var service = CreateService(out var store);

        var first = await service.GenerateAsync("ingest", allowedTriggerIds: null, principalId: null).ConfigureAwait(false);
        var second = await service.GenerateAsync("readback", allowedTriggerIds: null, principalId: null).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, store.Rows.Count, "A node holds many integration credentials, unlike the singleton MCP and proxy keys.");
        AssertEx.NotNull(await service.ValidateAsync(first.Key).ConfigureAwait(false));
        AssertEx.NotNull(await service.ValidateAsync(second.Key).ConfigureAwait(false));
    }

    [Test]
    public async Task GenerateAsync_WithoutAPrincipal_MintsANewPrincipalEachTime()
    {
        var service = CreateService(out _);

        var first = await service.GenerateAsync("one", allowedTriggerIds: null, principalId: null).ConfigureAwait(false);
        var second = await service.GenerateAsync("two", allowedTriggerIds: null, principalId: null).ConfigureAwait(false);

        AssertEx.NotEqual(Guid.Empty, first.View.PrincipalId);
        AssertEx.NotEqual(first.View.PrincipalId, second.View.PrincipalId);
    }

    [Test]
    public async Task GenerateAsync_WithAnExistingPrincipal_RotatesTheCredentialWithoutChangingTheIdentity()
    {
        // The rotation case ruling R4-6 exists for: a second credential for the same integrator inherits every session
        // and in-flight execution the first one owns, because ownership keys on the principal and not on the prefix.
        var service = CreateService(out _);
        var original = await service.GenerateAsync("ingest", allowedTriggerIds: null, principalId: null).ConfigureAwait(false);

        var rotated = await service.GenerateAsync("ingest-v2", allowedTriggerIds: null, original.View.PrincipalId).ConfigureAwait(false);

        AssertEx.Equal(original.View.PrincipalId, rotated.View.PrincipalId);
        AssertEx.NotEqual(original.View.KeyPrefix, rotated.View.KeyPrefix);

        var originalValidation = AssertEx.NotNull(await service.ValidateAsync(original.Key).ConfigureAwait(false));
        var rotatedValidation = AssertEx.NotNull(await service.ValidateAsync(rotated.Key).ConfigureAwait(false));
        AssertEx.Equal(originalValidation.PrincipalId, rotatedValidation.PrincipalId);
        AssertEx.NotEqual(originalValidation.KeyPrefix, rotatedValidation.KeyPrefix);
    }

    [Test]
    public async Task GenerateAsync_RoundTripsTheTriggerAllowlist_WithNullMeaningEveryTrigger()
    {
        var service = CreateService(out _);
        var allowed = new[]
        {
            Guid.NewGuid(),
            Guid.NewGuid()
        };

        var narrow = await service.GenerateAsync("narrow", allowed, principalId: null).ConfigureAwait(false);
        var broad = await service.GenerateAsync("broad", allowedTriggerIds: null, principalId: null).ConfigureAwait(false);

        AssertEx.True(allowed.SequenceEqual(AssertEx.NotNull(narrow.View.AllowedTriggerIds)), "The stored allowlist must round-trip in order.");
        AssertEx.Null(broad.View.AllowedTriggerIds, "A null allowlist is the wire form of 'every trigger'.");
        AssertEx.True(allowed.SequenceEqual(AssertEx.NotNull(AssertEx.NotNull(await service.ValidateAsync(narrow.Key).ConfigureAwait(false)).AllowedTriggerIds)),
            "Validation must hand the authorisation path the same allowlist that was stored.");
        AssertEx.Null(AssertEx.NotNull(await service.ValidateAsync(broad.Key).ConfigureAwait(false)).AllowedTriggerIds);
    }

    [Test]
    public void DeserializeAllowList_FailsClosedOnALiteralJsonNullAndOpenOnlyOnASqlNullColumn()
    {
        // Guid[] deserialised from the four characters `null` is a null reference, which read as "every trigger" and
        // silently widened a scoped credential to the whole node.
        AssertEx.Null(IntegrationApiKeyService.DeserializeAllowList(allowedTriggerIdsJson: null), "A SQL NULL column is the 'every trigger' credential.");
        AssertEx.Empty(AssertEx.NotNull(IntegrationApiKeyService.DeserializeAllowList("null")));
        AssertEx.Empty(AssertEx.NotNull(IntegrationApiKeyService.DeserializeAllowList("not json at all")));
    }

    [Test]
    public async Task ValidateAsync_AcceptsTheMintedKeyAndRejectsAWrongOneWithTheSamePrefix()
    {
        var service = CreateService(out _);
        var generated = await service.GenerateAsync("ingest", allowedTriggerIds: null, principalId: null).ConfigureAwait(false);

        AssertEx.NotNull(await service.ValidateAsync(generated.Key).ConfigureAwait(false));
        AssertEx.Null(await service.ValidateAsync(generated.View.KeyPrefix + "0000000000000000000000000000000000000000").ConfigureAwait(false),
            "A candidate sharing the display prefix but not the secret must fail on the digest.");
        AssertEx.Null(await service.ValidateAsync(generated.Key[..^1]).ConfigureAwait(false), "A truncated candidate must fail rather than match as a prefix.");
    }

    [Test]
    [Arguments("")]
    [Arguments("x")]
    [Arguments("xeint_")]
    [Arguments("xeint_abc")]
    [Arguments("Bearer xeint_abcdefgh")]
    [Arguments("xemcp_abcdefghijklmnop")]
    public async Task ValidateAsync_WithAValueShorterThanThePrefixOrOfAnotherScheme_ReturnsNullAndNeverThrows(string presented)
    {
        // The authentication handler calls this with whatever followed "Bearer " and does not wrap it: an unguarded
        // slice here is a 500 where a 401 is required, reachable by anyone who can reach the route.
        var service = CreateService(out _);
        _ = await service.GenerateAsync("ingest", allowedTriggerIds: null, principalId: null).ConfigureAwait(false);

        AssertEx.Null(await service.ValidateAsync(presented).ConfigureAwait(false));
    }

    [Test]
    public async Task ValidateAsync_WithNull_ReturnsNull()
    {
        var service = CreateService(out _);

        AssertEx.Null(await service.ValidateAsync(presented: null).ConfigureAwait(false));
    }

    [Test]
    public async Task ValidateAsync_AfterRevocation_FailsAndTheRowSurvives()
    {
        var service = CreateService(out var store);
        var generated = await service.GenerateAsync("ingest", allowedTriggerIds: null, principalId: null).ConfigureAwait(false);
        AssertEx.NotNull(await service.ValidateAsync(generated.Key).ConfigureAwait(false));

        AssertEx.True(await service.RevokeAsync(generated.View.Id).ConfigureAwait(false));

        AssertEx.Null(await service.ValidateAsync(generated.Key).ConfigureAwait(false), "A revoked credential must stop authenticating immediately.");
        AssertEx.Equal(expected: 1, store.Rows.Count, "Revocation is SOFT: execution and audit rows reference the prefix, so the row must survive.");
        AssertEx.True(store.Rows.Single().RevokedAtUtc is not null);
        AssertEx.True((await service.ListAsync().ConfigureAwait(false)).Single().RevokedAt is not null);
    }

    [Test]
    public async Task RevokeAsync_WithAnUnknownId_ReturnsFalse()
    {
        var service = CreateService(out _);

        AssertEx.False(await service.RevokeAsync(Guid.NewGuid()).ConfigureAwait(false));
    }

    [Test]
    public async Task ValidateAsync_StampsLastUsedOnSuccessOnly()
    {
        var service = CreateService(out var store);
        var generated = await service.GenerateAsync("ingest", allowedTriggerIds: null, principalId: null).ConfigureAwait(false);

        AssertEx.Null(await service.ValidateAsync(generated.View.KeyPrefix + "wrong-secret-material-here").ConfigureAwait(false));
        AssertEx.Null(store.Rows.Single().LastUsedAtUtc, "A failed validation must not record a use.");

        AssertEx.NotNull(await service.ValidateAsync(generated.Key).ConfigureAwait(false));
        AssertEx.Equal(SeedUnixMilliseconds, store.Rows.Single().LastUsedAtUtc);
    }

    private const long SeedUnixMilliseconds = 1_764_000_000_000;

    private static IIntegrationApiKeyService CreateService(out FakeIntegrationApiKeyStore store)
    {
        store = new FakeIntegrationApiKeyStore();
        return new IntegrationApiKeyService(store, new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(SeedUnixMilliseconds)));
    }
}
