namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     GGUF import mirrors the app-update desktop gate (see <see cref="AppUpdate.AppUpdateEndpointDesktopGateTests" />):
///     preview/start (<see cref="XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.PreviewGgufImportEndpoint" />,
///     <see cref="XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.StartGgufImportEndpoint" />) implement
///     <c>IDesktopOnlyEndpoint</c> and must be entirely ABSENT off the desktop flag, while capability/list/status/cancel
///     do not implement the marker and must stay reachable headless (per
///     <see cref="GgufImportEndpointContractTests.OnlyPreviewAndStartAreDesktopOnly" />). The default test host runs
///     non-desktop, so a POST to preview/start is rejected by routing (404/405 — no endpoint mapped) rather than
///     reaching the handler.
/// </summary>
public sealed class GgufImportEndpointDesktopGateTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    private const string ApiPrefix = "/api/local/v1";

    [Test]
    public async Task PreviewAndStartImportEndpoints_WhenNotDesktop_AreUnmapped()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        foreach (var route in new[]
                 {
                     $"{ApiPrefix}/model-fit/gguf/import/preview",
                     $"{ApiPrefix}/model-fit/gguf/import"
                 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route);
            factory.AddNodeBearerToken(request);
            request.Headers.Add("Origin", "http://localhost");

            using var response = await client.SendAsync(request).ConfigureAwait(false);

            // Unmapped POST path → routing rejects it. A registered endpoint with a valid operator token would have
            // returned 200/400; 404/405 proves the endpoint was never mapped off the desktop flag.
            AssertEx.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{route} should be unmapped off desktop, but returned {response.StatusCode}");
        }
    }

    /// <summary>
    ///     Only the headless half is provable through a real host: with <c>XE_LAUNCH_MODE=desktop</c> the actual entry
    ///     point hands off to the desktop (Velopack/WebView) launch path and never builds a test-hostable
    ///     <c>IHost</c>, and the env var is process-wide so flipping it races every concurrently-building factory.
    ///     The desktop-true half is covered at the seam instead: the endpoint returns
    ///     <c>DesktopLaunch.IsDesktopMode(...)</c> verbatim, whose env/arg/managed-install true-paths are proven by
    ///     <see cref="Hosting.DesktopLaunchTests" />; the composed behavior is exercised by the live desktop smoke.
    /// </summary>
    [Test]
    public async Task Capability_WhenNotDesktop_ReturnsAvailableFalse()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/model-fit/gguf/import/capability");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        AssertEx.False(doc.RootElement.GetProperty("available").GetBoolean(),
            "The default test host is not desktop-launched, so capability must report available:false.");
    }

    [Test]
    public async Task ImportsList_WhenNotDesktop_Responds()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/model-fit/gguf/imports");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        AssertEx.True(doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array,
            "Imports list must respond with an 'items' array even off the desktop flag.");
    }

    [Test]
    public async Task ImportStatusAndCancel_WhenNotDesktop_RespondForATrackedOperation()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        // Seed a real operation directly in the shared registry (the same singleton the import coordinator uses) so the
        // status/cancel routes have something to find — proving they are MAPPED and functional headless, not merely
        // returning a 404 that could equally mean "unmapped".
        var registry = factory.Services.GetRequiredService<IGgufAcquisitionOperationRegistry>();
        var registration = registry.Start(GgufAcquisitionOperationKind.Import, "desktop-gate-test-model");
        var operationId = registration.Status.OperationId;

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/model-fit/gguf/imports/{operationId}");
        factory.AddNodeBearerToken(statusRequest);
        using var statusResponse = await client.SendAsync(statusRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var statusJson = await statusResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var statusDoc = JsonDocument.Parse(statusJson);
        AssertEx.Equal(operationId, statusDoc.RootElement.GetProperty("operationId").GetGuid());

        using var cancelRequest = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/model-fit/gguf/imports/{operationId}/cancel");
        factory.AddNodeBearerToken(cancelRequest);
        // The cancel endpoint binds a request DTO, so FastEndpoints requires a JSON content type even though the
        // operation id rides in the route — mirror the generated client's empty JSON body.
        cancelRequest.Content = JsonContent.Create(new
        {
        });
        using var cancelResponse = await client.SendAsync(cancelRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
        var cancelJson = await cancelResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var cancelDoc = JsonDocument.Parse(cancelJson);
        AssertEx.Equal(operationId, cancelDoc.RootElement.GetProperty("operationId").GetGuid());
        AssertEx.True(cancelDoc.RootElement.GetProperty("cancellationRequested").GetBoolean(),
            "Cancelling a freshly-started, still-active operation must request cancellation.");
    }
}
