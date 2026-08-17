namespace XE_Local_AI_Engine.Tests.Providers.Training;

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using XE_Local_AI_Engine.Providers.Training;
using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Providers.HuggingFace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     uv is a managed binary acquired by digest, so a served archive that does not match the pin must be discarded
///     rather than unpacked. These run entirely against a stubbed handler — nothing reaches GitHub.
/// </summary>
public sealed class UvBinaryAcquirerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-uv-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task EnsureUv_WhenTheDigestDoesNotMatchThePin_RejectsAndUnpacksNothing()
    {
        var archive = BuildUvArchive();
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive)
        });
        using var http = new HttpClient(handler, disposeHandler: false);

        var exception = await AssertEx.ThrowsAsync<TrainingRuntimeException>(() => new UvBinaryAcquirer(http).EnsureUvAsync(_root, _ => { }, CancellationToken.None));

        AssertEx.Contains(exception.Message, "integrity");
        AssertEx.False(Directory.Exists(Path.Combine(_root, "uv", TrainingRuntimePins.UvVersion)),
            "An archive that failed verification must never be extracted.");
    }

    [Test]
    public async Task EnsureUv_WhenAlreadyCached_ReturnsThePathWithoutAnyRequest()
    {
        TrainingRuntimeTestInfrastructure.SeedCachedUv(_root);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler(static (_, _) =>
            throw new InvalidOperationException("A cache hit must not reach the network."));
        using var http = new HttpClient(handler, disposeHandler: false);

        var path = await new UvBinaryAcquirer(http).EnsureUvAsync(_root, _ => { }, CancellationToken.None);

        AssertEx.True(File.Exists(path));
        AssertEx.Equal(0, handler.CallCount);
    }

    [Test]
    public async Task EnsureUv_WhenTheDownloadFails_SurfacesASanitizedMessage()
    {
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler(static (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler, disposeHandler: false);

        var exception = await AssertEx.ThrowsAsync<TrainingRuntimeException>(() => new UvBinaryAcquirer(http).EnsureUvAsync(_root, _ => { }, CancellationToken.None));

        AssertEx.Contains(exception.Message, "network connection");
    }

    // A well-formed tar.gz laid out like the real release asset, but with contents whose digest cannot match the pin.
    private static byte[] BuildUvArchive()
    {
        var staging = Path.Combine(Path.GetTempPath(), "xe-uv-src-" + Guid.NewGuid().ToString("N"));
        var inner = Path.Combine(staging, TrainingRuntimePins.UvArchiveRootDirectory);
        _ = Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, TrainingRuntimePins.UvExecutableName), "not the real uv");

        try
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
            {
                TarFile.CreateFromDirectory(staging, gzip, includeBaseDirectory: false);
            }

            return buffer.ToArray();
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }
}
