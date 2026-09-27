namespace XE_Local_AI_Engine.Tests.Providers.Python;

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.Python;
using XE_Local_AI_Engine.Providers.Python.Implementation;
using XE_Local_AI_Engine.Tests.Providers.HuggingFace;
using XE_Local_AI_Engine.Tests.Providers.Training;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     uv is a managed binary acquired by digest, so a served archive that does not match the pin must be discarded
///     rather than unpacked. These run entirely against a stubbed handler — nothing reaches GitHub.
/// </summary>
[Category(TestCategories.Unit)]
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

        var exception = await AssertEx.ThrowsAsync<ManagedPythonException>(() => new UvBinaryAcquirer(http).EnsureUvAsync(_root, _ => { }, CancellationToken.None));

        AssertEx.Contains(exception.Message, "integrity");
        AssertEx.False(Directory.Exists(Path.Combine(_root, "uv", ManagedPythonPins.UvVersion)),
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

        var exception = await AssertEx.ThrowsAsync<ManagedPythonException>(() => new UvBinaryAcquirer(http).EnsureUvAsync(_root, _ => { }, CancellationToken.None));

        AssertEx.Contains(exception.Message, "network connection");
    }

    [Test]
    public async Task EnsureUv_WhenAnotherAcquirerLandsFirst_AdoptsItsTree_AndNeverReplacesIt()
    {
        // The cold-start race: a sibling host (or instance) completes its extract while this one downloads. Replacing the
        // winner's tree deleted a uv another caller had just been handed.
        var archive = BuildUvArchive("ours");
        var versionDir = Path.Combine(_root, "uv", ManagedPythonPins.UvVersion);
        var winner = Path.Combine(versionDir, ManagedPythonPins.Current.ArchiveRootDirectory, ManagedPythonPins.Current.ExecutableName);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) =>
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(winner)!);
            File.WriteAllText(winner, "winner");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            };
        });
        using var http = new HttpClient(handler, disposeHandler: false);

        var path = await new UvBinaryAcquirer(http, Sha256(archive)).EnsureUvAsync(_root, _ => { }, CancellationToken.None);

        AssertEx.Equal(winner, path);
        AssertEx.Equal("winner", await File.ReadAllTextAsync(path), "the winner's tree must be adopted as is");
        AssertEx.Equal(versionDir, string.Join(" | ", Directory.GetDirectories(Path.Combine(_root, "uv"))), "the losing staging tree is cleaned up");
    }

    [Test]
    public async Task EnsureUv_WhenTheTargetHasNoExecutable_ReplacesIt()
    {
        var archive = BuildUvArchive("ours");
        var versionDir = Path.Combine(_root, "uv", ManagedPythonPins.UvVersion);
        _ = Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(Path.Combine(versionDir, "debris"), "a tree without uv in it");
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive)
        });
        using var http = new HttpClient(handler, disposeHandler: false);

        var path = await new UvBinaryAcquirer(http, Sha256(archive)).EnsureUvAsync(_root, _ => { }, CancellationToken.None);

        AssertEx.Equal("ours", await File.ReadAllTextAsync(path));
        AssertEx.False(File.Exists(Path.Combine(versionDir, "debris")));
        AssertEx.Equal(versionDir, string.Join(" | ", Directory.GetDirectories(Path.Combine(_root, "uv"))), "the broken tree is moved aside and removed");
    }

    [Test]
    public async Task EnsureUv_TwoAcquirersOverABrokenTarget_BothReturnAnExecutableThatExists_AndTheTreeEndsComplete()
    {
        // Without the acquire lock: B saw the broken tree, A renamed it aside and landed a good one, then B renamed A's GOOD
        // tree aside and deleted it. Under the lock the second holder re-checks, finds A's tree and adopts it.
        var archive = BuildUvArchive("ours");
        var versionDir = Path.Combine(_root, "uv", ManagedPythonPins.UvVersion);
        _ = Directory.CreateDirectory(Path.Combine(versionDir, ManagedPythonPins.Current.ArchiveRootDirectory));
        await File.WriteAllTextAsync(Path.Combine(versionDir, "debris"), "a tree without uv in it");
        using var handler = new ArchiveHandler(archive);
        using var http = new HttpClient(handler, disposeHandler: false);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            var path = await new UvBinaryAcquirer(http, Sha256(archive)).EnsureUvAsync(_root, _ => { }, CancellationToken.None);
            return (path, exists: File.Exists(path));
        })));

        AssertEx.True(results.All(static result => result.exists), "both callers must be handed a uv that is on disk");
        AssertEx.Equal(results[0].path, results[1].path);
        AssertEx.Equal("ours", await File.ReadAllTextAsync(results[0].path));
        AssertEx.False(File.Exists(Path.Combine(versionDir, "debris")), "the broken tree is replaced, not merged into");
        AssertEx.Equal(1, handler.Requests, "the second holder adopts the first one's tree instead of downloading again");
        AssertEx.Equal(versionDir, string.Join(" | ", Directory.GetDirectories(Path.Combine(_root, "uv"))));
    }

    [Test]
    public async Task EnsureUv_ConcurrentAcquirersOnOneEmptyStore_EachReturnAnExecutableThatExists()
    {
        var archive = BuildUvArchive("ours");
        using var handler = new ArchiveHandler(archive);
        using var http = new HttpClient(handler, disposeHandler: false);

        var acquisitions = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var path = await new UvBinaryAcquirer(http, Sha256(archive)).EnsureUvAsync(_root, _ => { }, CancellationToken.None);
            // Checked at the moment the caller is handed the path: that is when the old delete-then-move lost it.
            return (path, exists: File.Exists(path));
        }));

        var results = await Task.WhenAll(acquisitions);

        AssertEx.True(results.All(static result => result.exists), "every caller must be handed a uv that is on disk");
        AssertEx.Equal(1, results.Select(static result => result.path).Distinct(StringComparer.Ordinal).Count());
    }

    [Test]
    public async Task EnsureUv_WithTheWindowsZip_ExtractsTheFlatLayout()
    {
        var archive = BuildZip(("uv.exe", "ours"), ("uvx.exe", "sibling"));
        using var handler = new ArchiveHandler(archive);
        using var http = new HttpClient(handler, disposeHandler: false);

        var path = await new UvBinaryAcquirer(http, WindowsAsset(archive)).EnsureUvAsync(_root, _ => { }, CancellationToken.None);

        AssertEx.Equal(Path.Combine(_root, "uv", ManagedPythonPins.UvVersion, "uv.exe"), path);
        AssertEx.Equal("ours", await File.ReadAllTextAsync(path));
        AssertEx.Equal(Path.Combine(_root, "uv", ManagedPythonPins.UvVersion), string.Join(" | ", Directory.GetDirectories(Path.Combine(_root, "uv"))));
    }

    [Test]
    [Arguments("../escaped")]
    [Arguments("..\\escaped")]
    [Arguments("/escaped")]
    public async Task EnsureUv_WhenAZipEntryEscapesTheStagingDirectory_RejectsTheWholeArchive(string escapingEntry)
    {
        var archive = BuildZip(("uv.exe", "ours"), (escapingEntry, "zip-slip"));
        using var handler = new ArchiveHandler(archive);
        using var http = new HttpClient(handler, disposeHandler: false);

        var exception = await AssertEx.ThrowsAsync<ManagedPythonException>(() =>
            new UvBinaryAcquirer(http, WindowsAsset(archive)).EnsureUvAsync(_root, _ => { }, CancellationToken.None));

        AssertEx.Contains(exception.Message, "outside its install directory");
        AssertEx.False(File.Exists(Path.Combine(_root, "uv", "escaped")), "the escaping entry must never be written");
        AssertEx.False(File.Exists("/escaped"), "an absolute entry must never be written");
        AssertEx.Empty(Directory.GetDirectories(Path.Combine(_root, "uv")), "neither the version directory nor the staging tree may remain");
    }

    [Test]
    public async Task EnsureUv_WithTheWindowsZip_WhenAnotherAcquirerLandsFirst_AdoptsItsTree()
    {
        var archive = BuildZip(("uv.exe", "ours"));
        var asset = WindowsAsset(archive);
        var winner = asset.ExecutablePath(_root);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) =>
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(winner)!);
            File.WriteAllText(winner, "winner");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            };
        });
        using var http = new HttpClient(handler, disposeHandler: false);

        var path = await new UvBinaryAcquirer(http, asset).EnsureUvAsync(_root, _ => { }, CancellationToken.None);

        AssertEx.Equal(winner, path);
        AssertEx.Equal("winner", await File.ReadAllTextAsync(path), "the winner's tree must be adopted as is");
        AssertEx.Equal(Path.GetDirectoryName(winner)!, string.Join(" | ", Directory.GetDirectories(Path.Combine(_root, "uv"))), "the losing staging tree is cleaned up");
    }

    private static ManagedPythonUvAsset WindowsAsset(byte[] archive)
    {
        return ManagedPythonPins.WindowsX64 with { Sha256 = Sha256(archive) };
    }

    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static string Sha256(byte[] content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    // A well-formed tar.gz laid out like the real release asset, but with contents whose digest cannot match the pin.
    private static byte[] BuildUvArchive(string executableContent = "not the real uv")
    {
        var staging = Path.Combine(Path.GetTempPath(), "xe-uv-src-" + Guid.NewGuid().ToString("N"));
        var inner = Path.Combine(staging, ManagedPythonPins.Current.ArchiveRootDirectory);
        _ = Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, ManagedPythonPins.Current.ExecutableName), executableContent);

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

    /// <summary>Serves one archive to any number of concurrent requests.</summary>
    private sealed class ArchiveHandler : HttpMessageHandler
    {
        private readonly byte[] _archive;
        private int _requests;

        public ArchiveHandler(byte[] archive)
        {
            _archive = archive;
        }

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_archive)
            });
        }
    }
}
