namespace XE_Local_AI_Engine.Tests.Endpoints.ExternalProviders;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Every external-provider route is Operator-gated, and the gate runs BEFORE anything the request could otherwise
///     cause.
/// </summary>
/// <remarks>
///     The substitutes are what make this a real test rather than a status-code check. Two of these routes have side
///     effects an unauthenticated caller must never reach: the read path decrypts the store that holds every API key,
///     and the probe makes an OUTBOUND request to an operator-supplied address — an unauthenticated probe would turn
///     the node into an SSRF instrument for anything that can reach its loopback port. So each assertion pairs the 401
///     with "and the store was never read / the probe was never attempted".
/// </remarks>
public sealed class ExternalProviderEndpointSecurityTests
{
    [Test]
    public async Task ExternalProviderEndpoints_WhenTokenMissing_AreRejectedBeforeAnyStoreReadOrOutboundCall()
    {
        var store = Substitute.For<IExternalProviderStore>();
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        var probeService = Substitute.For<IExternalProviderProbeService>();
        await using var factory = CreateFactory(store, administrationService, probeService);
        using var client = factory.CreateClient();

        using var listResponse = await client.GetAsync("/api/local/v1/external-providers/connections").ConfigureAwait(false);
        using var getResponse = await client.GetAsync("/api/local/v1/external-providers/connections/unsloth-box").ConfigureAwait(false);
        using var saveResponse = await client.PutAsJsonAsync("/api/local/v1/external-providers/connections/unsloth-box",
            new SaveExternalProviderConnectionRequest
            {
                DisplayName = "Unsloth box",
                BaseUrl = "http://127.0.0.1:18099",
                Locality = "Local"
            }).ConfigureAwait(false);
        using var deleteResponse = await client.DeleteAsync("/api/local/v1/external-providers/connections/unsloth-box").ConfigureAwait(false);
        using var probeResponse = await client.PostAsJsonAsync("/api/local/v1/external-providers/probe", new ExternalProviderProbeRequest
        {
            BaseUrl = "http://127.0.0.1:18099"
        }).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, listResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, getResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, saveResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, deleteResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, probeResponse.StatusCode);

        await store.DidNotReceiveWithAnyArgs().LoadAsync(Arg.Any<CancellationToken>());
        await administrationService.DidNotReceiveWithAnyArgs()
                                   .SaveConnectionAsync(Arg.Any<ExternalProviderConnectionSaveRequest>(), Arg.Any<CancellationToken>());
        await administrationService.DidNotReceiveWithAnyArgs().DeleteConnectionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await probeService.DidNotReceiveWithAnyArgs().ProbeAsync(Arg.Any<ExternalProviderProbeQuery>(), Arg.Any<CancellationToken>());
    }

    private static TestServerWebAppFactory CreateFactory(IExternalProviderStore store,
        IExternalProviderAdministrationService administrationService,
        IExternalProviderProbeService probeService)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IExternalProviderStore>();
                services.AddSingleton(store);
                services.RemoveAll<IExternalProviderAdministrationService>();
                services.AddSingleton(administrationService);
                services.RemoveAll<IExternalProviderProbeService>();
                services.AddSingleton(probeService);
            }
        };
    }
}
