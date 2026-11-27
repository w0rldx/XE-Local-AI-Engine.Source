namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The size cap of the shared runtime archive install, which no pinned asset comes near and so no binary manager test reaches.</summary>
[Category(TestCategories.Unit)]
public sealed class ManagedRuntimeArchiveInstallerTests
{
    [Test]
    public async Task Install_ABodyOverTheSizeCap_IsRetriedOnce_ThenSurfacesTheCapThroughTheFactory_AndPublishesNothing()
    {
        using var cache = new TempDirectory("xe-archive-installer");
        var targetDir = Path.Combine(cache.Path, "runtime", "cpu");
        using var handler = new CountingHttpMessageHandler(static () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[16])
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var installer = new ManagedRuntimeArchiveInstaller(http,
            "example.cpp",
            "example",
            maxDownloadBytes: 8,
            static (message, inner) => new InvalidDataException(message, inner),
            NullLogger.Instance);

        var exception = await AssertEx.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(new Uri("https://example.invalid/runtime.zip"),
            "runtime.zip",
            new string('0', 64),
            ManagedRuntimeArchiveKind.Zip,
            targetDir,
            CancellationToken.None));

        AssertEx.Equal(expected: 2, handler.CallCount, "an over-cap download is retried exactly once");
        AssertEx.Equal("The example.cpp runtime could not be downloaded or failed integrity verification after a retry. Check the network connection and try again.",
            exception.Message);
        AssertEx.Equal("The example.cpp runtime download exceeded the maximum allowed size.", AssertEx.NotNull(exception.InnerException).Message);
        AssertEx.False(Directory.Exists(targetDir), "a failed install publishes nothing");
    }
}
