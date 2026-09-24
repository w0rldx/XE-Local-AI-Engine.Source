namespace XE_Local_AI_Engine.Tests.ExternalApps;

using System.Reflection;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The bridge token is a live credential for this node's inference surface, and the one thing that must never
///     happen to it is reaching a browser. The instance store surfaces it because Start has to re-inject it into a
///     rebuilt container; every layer above the store must drop it, and these are the assertions that say so rather
///     than trusting that each mapper was written by hand with that in mind.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ExternalAppBridgeTokenExposureTests
{
    private const string AppId = "test-app";

    /// <summary>
    ///     The projections the endpoints return. A member carrying the token would be serialized straight into the
    ///     SPA's instance list, so no member may be shaped to hold it at all.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ProjectionTypes))]
    public void ProjectionTypes_DeclareNoMemberThatCouldCarryTheBridgeToken(Type projection)
    {
        var offenders = projection.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                  .Where(static property => property.Name.Contains("BridgeToken", StringComparison.OrdinalIgnoreCase)
                                                            || property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase))
                                  .Select(static property => property.Name)
                                  .ToArray();

        AssertEx.Empty(offenders, $"'{projection.Name}' is returned to the browser and must carry nothing token-shaped: {string.Join(", ", offenders)}");
    }

    /// <summary>
    ///     The end-to-end statement: install an application, then serialize exactly what the detail endpoint returns
    ///     and search it for the token the row actually holds. A reflection check alone would pass on a mapper that
    ///     stuffed the token into a free-form string field.
    /// </summary>
    [Test]
    public async Task InstanceDetail_NeverCarriesTheInstancesBridgeToken()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);
        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);
        var admitted = await harness.Service.InstallAsync(new InstallCommand(AppId,
            DisplayName: null,
            manifest.ManifestVersion,
            manifest.ManifestSha256,
            new Dictionary<string, string>(StringComparer.Ordinal),
            AcceptPermissions: true));
        await harness.WaitUntilIdleAsync(admitted.Id);

        var token = AssertEx.NotNull(AssertEx.NotNull(await harness.ReadAsync(admitted.Id)).BridgeToken);
        var detail = await harness.Service.GetAsync(admitted.Id);

        var serialized = JsonSerializer.Serialize(detail);
        AssertEx.False(serialized.Contains(token, StringComparison.Ordinal), "The instance detail the SPA renders must not carry the application's bridge credential.");
        AssertEx.False(detail.ToString().Contains(token, StringComparison.Ordinal), "Nor may any log line that formats it.");
    }

    public static IEnumerable<Func<Type>> ProjectionTypes()
    {
        yield return static () => typeof(ExternalAppInstanceDetail);
        yield return static () => typeof(ExternalAppInstanceSummary);
    }
}
