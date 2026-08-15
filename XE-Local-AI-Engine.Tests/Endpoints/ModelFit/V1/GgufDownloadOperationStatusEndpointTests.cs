namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     HTTP-layer coverage for <c>GET model-fit/gguf/downloads/operations/{operationId:guid}</c>
///     (<see cref="XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.GetGgufDownloadOperationStatusEndpoint" />), which
///     sits alongside the older <c>GET model-fit/gguf/downloads/{modelName}</c> route on the same path prefix. Both
///     endpoints share the singleton <see cref="IGgufAcquisitionOperationRegistry" /> that the download AND import
///     coordinators both write into — <see cref="GetGgufDownloadOperationStatusEndpoint" /> filters to
///     <see cref="GgufAcquisitionOperationKind.Download" /> the same way
///     <c>GgufDownloadCoordinator.GetStatus(Guid)</c> does, so an import operation id is a genuine 404, not merely an
///     unknown guid. Tests seed operations directly through the shared registry (a production DI singleton) rather than
///     a real download/import, keeping the host hermetic.
/// </summary>
public sealed class GgufDownloadOperationStatusEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    private const string ApiPrefix = "/api/local/v1";

    [Test]
    public async Task ForADownloadOperation_ReturnsSameStatusAsTheModelNameView()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<IGgufAcquisitionOperationRegistry>();
        var registration = registry.Start(GgufAcquisitionOperationKind.Download, "operations-route-test-model", totalBytes: 1024);
        var operationId = registration.Status.OperationId;

        using var byOperationRequest =
            new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/model-fit/gguf/downloads/operations/{operationId}");
        factory.AddNodeBearerToken(byOperationRequest);
        using var byOperationResponse = await client.SendAsync(byOperationRequest).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, byOperationResponse.StatusCode);
        var byOperationJson = await byOperationResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var byOperationDoc = JsonDocument.Parse(byOperationJson);

        using var byModelNameRequest =
            new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/model-fit/gguf/downloads/operations-route-test-model");
        factory.AddNodeBearerToken(byModelNameRequest);
        using var byModelNameResponse = await client.SendAsync(byModelNameRequest).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, byModelNameResponse.StatusCode);
        var byModelNameJson = await byModelNameResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var byModelNameDoc = JsonDocument.Parse(byModelNameJson);

        AssertEx.Equal(operationId, byOperationDoc.RootElement.GetProperty("operationId").GetGuid());
        AssertEx.Equal(byModelNameDoc.RootElement.GetProperty("operationId").GetGuid(),
            byOperationDoc.RootElement.GetProperty("operationId").GetGuid());
        AssertEx.Equal(byModelNameDoc.RootElement.GetProperty("modelName").GetString()!,
            byOperationDoc.RootElement.GetProperty("modelName").GetString());
        AssertEx.Equal(byModelNameDoc.RootElement.GetProperty("phase").GetString()!,
            byOperationDoc.RootElement.GetProperty("phase").GetString());
        AssertEx.Equal(byModelNameDoc.RootElement.GetProperty("totalBytes").GetInt64(),
            byOperationDoc.RootElement.GetProperty("totalBytes").GetInt64());
    }

    [Test]
    public async Task ForAnImportOperationId_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<IGgufAcquisitionOperationRegistry>();
        var registration = registry.Start(GgufAcquisitionOperationKind.Import, "operations-route-import-model");
        var importOperationId = registration.Status.OperationId;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/model-fit/gguf/downloads/operations/{importOperationId}");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        // The download-operations route must never leak an import's status: GgufDownloadCoordinator.GetStatus(Guid)
        // filters to OperationKind.Download, so a real (but Import-kind) operation id 404s here even though the
        // registry genuinely tracks it.
        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task DownloadsRoute_WhenModelNameIsLiterallyOperations_IsNotCapturedByTheOperationsRoute()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        // No model is ever tracked under the literal name "operations", so the single-segment {modelName} route must
        // 404 rather than being swallowed by, or colliding with, the sibling two-segment operations/{operationId:guid}
        // route (which requires a second, guid-constrained segment this request does not supply).
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/model-fit/gguf/downloads/operations");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
