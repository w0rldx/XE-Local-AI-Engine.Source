namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class GgufImportEndpointContractTests
{
    [Test]
    public void OnlyPreviewAndStartAreDesktopOnly()
    {
        AssertEx.True(typeof(IDesktopOnlyEndpoint).IsAssignableFrom(typeof(PreviewGgufImportEndpoint)));
        AssertEx.True(typeof(IDesktopOnlyEndpoint).IsAssignableFrom(typeof(StartGgufImportEndpoint)));
        AssertEx.False(typeof(IDesktopOnlyEndpoint).IsAssignableFrom(typeof(GetGgufImportCapabilityEndpoint)));
        AssertEx.False(typeof(IDesktopOnlyEndpoint).IsAssignableFrom(typeof(GetGgufImportsEndpoint)));
        AssertEx.False(typeof(IDesktopOnlyEndpoint).IsAssignableFrom(typeof(GetGgufImportStatusEndpoint)));
        AssertEx.False(typeof(IDesktopOnlyEndpoint).IsAssignableFrom(typeof(CancelGgufImportEndpoint)));
    }

    [Test]
    public void ImportRoutesAreAdditiveAndDownloadHubRemainsStable()
    {
        AssertEx.Equal("model-fit/gguf/import/capability", LocalApiRoutes.ModelFit.ImportCapability);
        AssertEx.Equal("model-fit/gguf/import/preview", LocalApiRoutes.ModelFit.ImportPreview);
        AssertEx.Equal("model-fit/gguf/import", LocalApiRoutes.ModelFit.Import);
        AssertEx.Equal("model-fit/gguf/imports", LocalApiRoutes.ModelFit.Imports);
        AssertEx.Equal("/api/local/v1/model-fit/gguf/downloads/hub", LocalApiRoutes.ModelFit.DownloadHub);
        AssertEx.Equal("model-fit/gguf/downloads/operations/{operationId:guid}", LocalApiRoutes.ModelFit.DownloadOperationStatus);
    }
}
