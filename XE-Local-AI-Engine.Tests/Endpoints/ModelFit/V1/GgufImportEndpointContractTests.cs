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

    // ──────────────────────────────────────────────────────────────────────
    // All six import routes declare the Operator policy in Configure() — source-text scan, same mechanism as
    // AppUpdateContractTests.PublicAppUpdateEndpoints_AreOperatorGated. A reflection-based Policies() check isn't
    // available (FastEndpoints resolves policies into route metadata at endpoint registration, not as a queryable
    // attribute), so this locks the literal Configure() call instead — it fails the moment someone deletes or
    // conditions the line.
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AllSixImportRoutes_AreOperatorGated()
    {
        foreach (var fileName in new[]
                 {
                     "GetGgufImportCapabilityEndpoint.cs",
                     "PreviewGgufImportEndpoint.cs",
                     "StartGgufImportEndpoint.cs",
                     "GetGgufImportsEndpoint.cs",
                     "GetGgufImportStatusEndpoint.cs",
                     "CancelGgufImportEndpoint.cs"
                 })
        {
            var source = await File.ReadAllTextAsync(GetEndpointPath(fileName));
            AssertEx.True(source.Contains("Policies(NodeAuthorizationPolicies.Operator)", StringComparison.Ordinal),
                $"{fileName} must declare Policies(NodeAuthorizationPolicies.Operator).");
        }
    }

    private static string GetEndpointPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "XE-Local-AI-Engine.Client", "Endpoints", "ModelFit", "V1", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate endpoint source {fileName}.");
    }
}
