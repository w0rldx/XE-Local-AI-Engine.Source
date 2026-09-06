namespace XE_Local_AI_Engine.Client.Persistence.Tests.Integrations;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Every method on the credential store. The two mutators deliberately use <c>ExecuteUpdate</c>, so the assertions
///     below also pin that neither of them re-seals the digest column.
/// </summary>
public sealed class IntegrationApiKeyStoreTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task CreateAsync_RoundTripsTheDigestAndSupportsManyKeysPerPrincipal()
    {
        using var fixture = new IntegrationTestFixture();
        var principalId = Guid.NewGuid();
        var firstDigest = SHA256.HashData("first"u8.ToArray());
        var secondDigest = SHA256.HashData("second"u8.ToArray());

        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var store = new IntegrationApiKeyStore(context, new FixedTimeProvider(FixedNow));
            _ = await store.CreateAsync(new IntegrationApiKeyCreateCommand(Guid.NewGuid(), principalId, "xeint_aaaaaaaa", firstDigest, "Ingest", null))
                           .ConfigureAwait(false);

            // Rotating or adding a credential joins the existing principal rather than creating a new identity — which
            // is the whole reason the two ids are separate.
            _ = await store.CreateAsync(new IntegrationApiKeyCreateCommand(Guid.NewGuid(),
                               principalId,
                               "xeint_bbbbbbbb",
                               secondDigest,
                               "Read",
                               """["4b1f0f2a-6f2f-4c1f-9d3e-7a4c0b5e8d21"]"""))
                           .ConfigureAwait(false);
        }

        await using var readContext = fixture.CreateContext();
        var readStore = new IntegrationApiKeyStore(readContext, new FixedTimeProvider(FixedNow));

        var listed = await readStore.ListAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 2, listed.Count);
        AssertEx.True(listed.All(row => row.PrincipalId == principalId), "A node holds many credentials, and several may belong to one integrator.");

        var byPrefix = AssertEx.NotNull(await readStore.GetByPrefixAsync("xeint_aaaaaaaa").ConfigureAwait(false));
        AssertEx.True(byPrefix.KeyHash.Span.SequenceEqual(firstDigest), "The authentication lookup must return the plaintext digest.");
        AssertEx.Null(byPrefix.AllowedTriggerIdsJson, "A null allowlist means every trigger.");
        AssertEx.Null(byPrefix.RevokedAtUtc);

        AssertEx.Null(await readStore.GetByPrefixAsync("xeint_nosuchkey").ConfigureAwait(false));
    }

    [Test]
    public async Task TouchLastUsedAsync_StampsTheTimestampWithoutRewritingTheSealedDigest()
    {
        using var fixture = new IntegrationTestFixture();
        var digest = SHA256.HashData("hot-path"u8.ToArray());
        var keyId = Guid.NewGuid();

        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new IntegrationApiKeyStore(context, new FixedTimeProvider(FixedNow));
        _ = await store.CreateAsync(new IntegrationApiKeyCreateCommand(keyId, Guid.NewGuid(), "xeint_aaaaaaaa", digest, "Ingest", null)).ConfigureAwait(false);

        var sealedBefore = await ReadSealedHashAsync(fixture, keyId).ConfigureAwait(false);

        AssertEx.True(await store.TouchLastUsedAsync(keyId, atUtc: 7_000).ConfigureAwait(false));
        AssertEx.False(await store.TouchLastUsedAsync(Guid.NewGuid(), atUtc: 7_000).ConfigureAwait(false));

        var sealedAfter = await ReadSealedHashAsync(fixture, keyId).ConfigureAwait(false);
        AssertEx.True(sealedBefore.AsSpan().SequenceEqual(sealedAfter),
            "A last-used stamp must not round-trip the credential through the interceptors — a fresh nonce on every authenticated request is exactly what this avoids.");

        await using var readContext = fixture.CreateContext();
        var read = AssertEx.NotNull(await new IntegrationApiKeyStore(readContext, new FixedTimeProvider(FixedNow)).GetByPrefixAsync("xeint_aaaaaaaa").ConfigureAwait(false));
        AssertEx.Equal(expected: 7_000L, read.LastUsedAtUtc);
        AssertEx.True(read.KeyHash.Span.SequenceEqual(digest), "And the digest must still decrypt afterwards.");
    }

    [Test]
    public async Task RevokeAsync_IsASoftStampThatDeletesNothingAndDoesNotRepeat()
    {
        using var fixture = new IntegrationTestFixture();
        var keyId = Guid.NewGuid();

        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new IntegrationApiKeyStore(context, new FixedTimeProvider(FixedNow));
        _ = await store.CreateAsync(new IntegrationApiKeyCreateCommand(keyId, Guid.NewGuid(), "xeint_aaaaaaaa", SHA256.HashData("k"u8.ToArray()), "Ingest", null))
                       .ConfigureAwait(false);

        AssertEx.True(await store.RevokeAsync(keyId, atUtc: 8_000).ConfigureAwait(false));
        AssertEx.False(await store.RevokeAsync(keyId, atUtc: 8_100).ConfigureAwait(false), "A second revoke matches no live row, so the first stamp stands.");
        AssertEx.False(await store.RevokeAsync(Guid.NewGuid(), atUtc: 8_100).ConfigureAwait(false));

        await using var readContext = fixture.CreateContext();
        var read = AssertEx.NotNull(await new IntegrationApiKeyStore(readContext, new FixedTimeProvider(FixedNow)).GetByPrefixAsync("xeint_aaaaaaaa").ConfigureAwait(false));
        AssertEx.Equal(expected: 8_000L, read.RevokedAtUtc);
        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("integration_api_keys").ConfigureAwait(false),
            "The row survives: execution and audit rows reference its prefix.");
    }

    private static async Task<byte[]> ReadSealedHashAsync(IntegrationTestFixture fixture, Guid keyId)
    {
        var value = await fixture.RawScalarAsync("SELECT key_hash FROM integration_api_keys WHERE id = $id;",
                                     command => command.Parameters.AddWithValue("$id", keyId))
                                 .ConfigureAwait(false);
        return AssertEx.NotNull(value as byte[]);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            now;
    }
}
