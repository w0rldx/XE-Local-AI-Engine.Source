namespace XE_Local_AI_Engine.Tests.Endpoints.ExternalApps.V1;

using System.Net;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The kill switch. External apps ship OFF, and off means the whole family answers 404 — ahead of authentication,
///     so it cannot be probed by status code, and ahead of the service, so a refused request touches no collaborator.
///     The routes stay DISCOVERED either way: what changes is behaviour, never the published surface
///     (<c>OpenApiDocumentTests.LocalOpenApiDocument_DescribesExternalAppSurface_WhenTheFeatureIsDisabled</c>).
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ExternalAppFeatureAvailabilityTests
{
    /// <summary>
    ///     All twenty method+route pairs of the family. Spelled out rather than built from <c>LocalApiRoutes</c>, so a
    ///     route that moved out from under the switch fails here instead of silently becoming reachable.
    /// </summary>
    [Test]
    [Arguments("GET", ExternalAppEndpointPayloads.Runtime)]
    [Arguments("POST", ExternalAppEndpointPayloads.RuntimeRefresh)]
    [Arguments("GET", ExternalAppEndpointPayloads.Catalog)]
    [Arguments("POST", ExternalAppEndpointPayloads.CatalogRefresh)]
    [Arguments("GET", ExternalAppEndpointPayloads.CatalogApplication)]
    [Arguments("GET", ExternalAppEndpointPayloads.InstallPreview)]
    [Arguments("GET", ExternalAppEndpointPayloads.Instances)]
    [Arguments("POST", ExternalAppEndpointPayloads.Instances)]
    [Arguments("GET", ExternalAppEndpointPayloads.Instance)]
    [Arguments("DELETE", ExternalAppEndpointPayloads.Instance)]
    [Arguments("GET", ExternalAppEndpointPayloads.InstanceUpdatePreview)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceStart)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceStop)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceRestart)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceReset)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceUpdate)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceCancel)]
    [Arguments("PUT", ExternalAppEndpointPayloads.InstanceVariables)]
    [Arguments("GET", ExternalAppEndpointPayloads.InstanceEvents)]
    [Arguments("GET", ExternalAppEndpointPayloads.InstanceLogs)]
    public async Task Route_OnADisabledNode_ReturnsNotFoundWithoutReachingTheService(string method, string route)
    {
        var apps = Substitute.For<IExternalAppService>();
        var catalog = Substitute.For<IApplicationCatalogProvider>();
        var reconciler = Substitute.For<IExternalAppStartupReconciler>();
        var store = Substitute.For<IExternalAppInstanceStore>();
        await using var factory = ExternalAppEndpointPayloads.DisabledFactory(apps, catalog, reconciler: reconciler, store: store);

        using var response = await ExternalAppEndpointPayloads.SendAsOperatorAsync(factory, method, route).ConfigureAwait(false);

        // 404 and never 401, 403 or 500: the switch sits ahead of authentication, so a disabled feature is
        // indistinguishable from one this build does not have.
        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, $"{method} {route} must be refused on a disabled node.");
        AssertEx.Empty(apps.ReceivedCalls(), $"{method} {route} reached the service.");
        AssertEx.Empty(catalog.ReceivedCalls());
        AssertEx.Empty(reconciler.ReceivedCalls());
        AssertEx.Empty(store.ReceivedCalls());
    }

    /// <summary>
    ///     The control for the case above: the same twenty pairs REACH the feature on an enabled node. "Reached" rather
    ///     than "was not a 404", because two of these routes answer 404 honestly when the stub has nothing to give — so
    ///     what is asserted is that the request either got past the middleware's refusal or touched a collaborator,
    ///     both of which are impossible while the switch is off.
    /// </summary>
    [Test]
    [Arguments("GET", ExternalAppEndpointPayloads.Runtime)]
    [Arguments("POST", ExternalAppEndpointPayloads.RuntimeRefresh)]
    [Arguments("GET", ExternalAppEndpointPayloads.Catalog)]
    [Arguments("POST", ExternalAppEndpointPayloads.CatalogRefresh)]
    [Arguments("GET", ExternalAppEndpointPayloads.CatalogApplication)]
    [Arguments("GET", ExternalAppEndpointPayloads.InstallPreview)]
    [Arguments("GET", ExternalAppEndpointPayloads.Instances)]
    [Arguments("POST", ExternalAppEndpointPayloads.Instances)]
    [Arguments("GET", ExternalAppEndpointPayloads.Instance)]
    [Arguments("DELETE", ExternalAppEndpointPayloads.Instance)]
    [Arguments("GET", ExternalAppEndpointPayloads.InstanceUpdatePreview)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceStart)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceStop)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceRestart)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceReset)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceUpdate)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceCancel)]
    [Arguments("PUT", ExternalAppEndpointPayloads.InstanceVariables)]
    [Arguments("GET", ExternalAppEndpointPayloads.InstanceEvents)]
    [Arguments("GET", ExternalAppEndpointPayloads.InstanceLogs)]
    public async Task Route_OnAnEnabledNode_IsRouted(string method, string route)
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        apps.ListDetailsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var catalog = Substitute.For<IApplicationCatalogProvider>();
        catalog.GetCatalogAsync(Arg.Any<CancellationToken>()).Returns(ExternalAppEndpointPayloads.CatalogSnapshot());
        var store = Substitute.For<IExternalAppInstanceStore>();
        await using var factory = ExternalAppEndpointPayloads.EnabledFactory(apps, catalog, store: store);

        using var response = await ExternalAppEndpointPayloads.SendAsOperatorAsync(factory, method, route).ConfigureAwait(false);

        var reachedTheFeature = response.StatusCode != HttpStatusCode.NotFound
                                || apps.ReceivedCalls().Any()
                                || catalog.ReceivedCalls().Any()
                                || store.ReceivedCalls().Any();
        AssertEx.True(reachedTheFeature, $"{method} {route} must be routed on an enabled node, not refused by the switch.");
    }

    /// <summary>
    ///     The hub's negotiate shares the family's first segment, so the one prefix check covers it: a disabled node
    ///     refuses the handshake before authentication, and an enabled one completes it.
    /// </summary>
    [Test]
    [Arguments(false, HttpStatusCode.NotFound)]
    [Arguments(true, HttpStatusCode.OK)]
    public async Task HubNegotiate_FollowsTheFeatureFlag(bool enabled, HttpStatusCode expected)
    {
        await using var factory = enabled
            ? ExternalAppEndpointPayloads.EnabledFactory()
            : ExternalAppEndpointPayloads.DisabledFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, LocalApiRoutes.ExternalApps.Hub + "/negotiate?negotiateVersion=1")
        {
            Content = new StringContent(string.Empty)
        };
        request.Headers.Add("Origin", "http://localhost");
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(expected, response.StatusCode);
    }
}
