namespace XE_Local_AI_Engine.Tests.ExternalApps;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one place a bridge token becomes an identity. Driven through the REAL store over real SQLite and the real
///     encrypted column, because what is being asserted is that a token minted at install can be read back out of an
///     AEAD-sealed row and matched — a substituted store would prove none of that.
/// </summary>
public sealed class ExternalAppBridgeTokenVerifierTests
{
    private const string AppId = "test-app";

    [Test]
    public async Task Install_MintsATokenTheVerifierAccepts()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);
        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest).ConfigureAwait(false);
        var admitted = await harness.Service.InstallAsync(InstallCommandFor(manifest)).ConfigureAwait(false);
        await harness.WaitUntilIdleAsync(admitted.Id).ConfigureAwait(false);

        var token = AssertEx.NotNull(AssertEx.NotNull(await harness.ReadAsync(admitted.Id).ConfigureAwait(false)).BridgeToken,
            "Every install must mint a bridge token; a container with none can never reach the node's inference surface.");

        var caller = AssertEx.NotNull(await harness.VerifyBridgeTokenAsync(token).ConfigureAwait(false));

        AssertEx.Equal(admitted.Id, caller.InstanceId, "A verified token identifies the instance it was minted for.");
    }

    [Test]
    public async Task Verifier_RefusesAWrongSecretForARealInstance()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);
        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest).ConfigureAwait(false);
        var row = await harness.SeedAsync(manifest, ExternalAppInstanceStatus.Stopped).ConfigureAwait(false);

        var forged = $"{row.Id:N}.Zm9yZ2VkLXNlY3JldC1tYXRlcmlhbA";

        AssertEx.Null(await harness.VerifyBridgeTokenAsync(forged).ConfigureAwait(false),
            "Naming a real instance is not knowing its secret.");
    }

    /// <summary>
    ///     The whole token is compared, not just the secret half, so a token whose id was rewritten to name another
    ///     instance cannot match that instance's row either.
    /// </summary>
    [Test]
    public async Task Verifier_RefusesOneInstancesSecretPresentedUnderAnothersId()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);
        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest).ConfigureAwait(false);
        var victim = await harness.SeedAsync(manifest, ExternalAppInstanceStatus.Stopped).ConfigureAwait(false);
        var attacker = await harness.SeedAsync(manifest, ExternalAppInstanceStatus.Stopped).ConfigureAwait(false);

        var victimToken = AssertEx.NotNull(victim.BridgeToken);
        var secret = victimToken[(victimToken.IndexOf('.', StringComparison.Ordinal) + 1)..];

        AssertEx.Null(await harness.VerifyBridgeTokenAsync($"{attacker.Id:N}.{secret}").ConfigureAwait(false),
            "One instance's secret must not become another's by rewriting the id in front of it.");
    }

    [Test]
    public async Task Verifier_RefusesATokenForAnInstanceThatDoesNotExist()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);
        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest).ConfigureAwait(false);

        AssertEx.Null(await harness.VerifyBridgeTokenAsync(ContainerBridgeToken.Mint(Guid.NewGuid())).ConfigureAwait(false),
            "A well-formed token for an uninstalled instance names nothing.");
    }

    [Test]
    [Arguments("")]
    [Arguments("not-a-token")]
    [Arguments("not-a-guid.secret")]
    public async Task Verifier_RefusesAMalformedTokenWithoutReadingTheStore(string presented)
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);
        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest).ConfigureAwait(false);

        AssertEx.Null(await harness.VerifyBridgeTokenAsync(presented).ConfigureAwait(false));
    }

    /// <summary>
    ///     Uninstall has no separate revoke step, and must not need one: the token lives on the instance row, so
    ///     deleting the row is what makes it name nothing. This is the assertion that stands in for the code that
    ///     deliberately does not exist.
    /// </summary>
    [Test]
    public async Task Uninstall_RevokesTheTokenByDeletingTheRowThatCarriesIt()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);
        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest).ConfigureAwait(false);
        var row = await harness.SeedAsync(manifest, ExternalAppInstanceStatus.Stopped).ConfigureAwait(false);
        var token = AssertEx.NotNull(row.BridgeToken);

        AssertEx.NotNull(await harness.VerifyBridgeTokenAsync(token).ConfigureAwait(false), "The seeded instance's token must verify before the row is removed.");

        await harness.DeleteRowAsync(row.Id, row.Version).ConfigureAwait(false);

        AssertEx.Null(await harness.VerifyBridgeTokenAsync(token).ConfigureAwait(false),
            "The row's deletion IS the revoke; nothing else revokes a bridge token.");
    }

    private static InstallCommand InstallCommandFor(ApplicationManifest manifest)
    {
        return new InstallCommand(AppId,
            DisplayName: null,
            manifest.ManifestVersion,
            manifest.ManifestSha256,
            new Dictionary<string, string>(StringComparer.Ordinal),
            AcceptPermissions: true);
    }
}
