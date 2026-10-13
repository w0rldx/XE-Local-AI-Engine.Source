namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The SPA cache policy over a release-shaped web root: every file stamped with the ZIP's 1980 DOS epoch and a shell
///     whose length does not change between builds, so only its bytes tell two builds apart.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SpaStaticFileOptionsTests
{
    private static readonly DateTime DosEpoch = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A shell whose SHA-256 folds to a content date in 1970, earlier than the 1980 date an old build sent.</summary>
    private const string ShellHashedBefore1980 = "<p>shell 16</p>";

    [Test]
    public async Task UpdatedShell_OfEqualLengthAndMtime_IsNotAnsweredWith304()
    {
        using var oldRoot = new TempWebRoot("<script src=\"/assets/index-AAAAAAAA.js\"></script>");
        using var newRoot = new TempWebRoot("<script src=\"/assets/index-BBBBBBBB.js\"></script>");
        AssertEx.Equal(new FileInfo(oldRoot.Shell).Length, new FileInfo(newRoot.Shell).Length);

        EntityTagHeaderValue oldTag;
        using (var oldHost = await StartAsync(oldRoot.Files))
        {
            using var oldClient = oldHost.GetTestClient();
            using var first = await oldClient.GetAsync("/index.html");
            oldTag = first.Headers.ETag ?? throw new InvalidOperationException("The old shell carried no ETag.");
        }

        using var newHost = await StartAsync(newRoot.Files);
        using var client = newHost.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/index.html");
        request.Headers.IfNoneMatch.Add(oldTag);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.NotEqual(oldTag, response.Headers.ETag);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "index-BBBBBBBB.js");
    }

    [Test]
    public async Task UnchangedShell_RevalidatesTo304()
    {
        using var root = new TempWebRoot("<p>shell</p>");
        using var host = await StartAsync(root.Files);
        using var client = host.GetTestClient();

        using var first = await client.GetAsync("/index.html");
        using var second = await client.GetAsync("/index.html");
        AssertEx.Equal(first.Headers.ETag, second.Headers.ETag);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/index.html");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var conditional = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.NotModified, conditional.StatusCode);
    }

    [Test]
    [Arguments("/assets/index-AAAAAAAA.js", SpaStaticFileOptions.HashedAssetCacheControl, true)]
    [Arguments("/assets/page.html", SpaStaticFileOptions.ShellCacheControl, false)]
    [Arguments("/favicon.png", SpaStaticFileOptions.ShellCacheControl, false)]
    [Arguments("/manifest.json", SpaStaticFileOptions.ShellCacheControl, false)]
    [Arguments("/index.html", SpaStaticFileOptions.ShellCacheControl, false)]
    [Arguments("/settings/models", SpaStaticFileOptions.ShellCacheControl, false)]
    public async Task CacheControl_IsImmutableOnlyForHashedAssets_WhichAloneKeepLastModified(string path, string expected, bool keepsLastModified)
    {
        using var root = new TempWebRoot("<p>shell</p>");
        using var host = await StartAsync(root.Files);
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(path);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected, response.Headers.CacheControl?.ToString());
        AssertEx.NotNull(response.Headers.ETag);
        AssertEx.Equal(keepsLastModified, response.Content.Headers.LastModified is not null, $"{path}: Last-Modified presence.");

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        using var conditional = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.NotModified, conditional.StatusCode);
        AssertEx.Equal(keepsLastModified, conditional.Content.Headers.LastModified is not null, $"{path}: Last-Modified presence on 304.");
    }

    [Test]
    public async Task HtmlUnderAssets_GetsAContentETag()
    {
        using var oldRoot = new TempWebRoot("<p>shell</p>", "<p>page A</p>");
        using var newRoot = new TempWebRoot("<p>shell</p>", "<p>page B</p>");

        using var oldHost = await StartAsync(oldRoot.Files);
        using var newHost = await StartAsync(newRoot.Files);
        using var oldClient = oldHost.GetTestClient();
        using var newClient = newHost.GetTestClient();
        using var oldPage = await oldClient.GetAsync("/assets/page.html");
        using var newPage = await newClient.GetAsync("/assets/page.html");

        AssertEx.NotEqual(oldPage.Headers.ETag, newPage.Headers.ETag, "Equal-length, equal-mtime HTML under /assets must not share an ETag.");
    }

    [Test]
    public void UnreadableFile_IsServedUnstamped()
    {
        var file = Substitute.For<IFileInfo>();
        file.Exists.Returns(true);
        file.Length.Returns(4);
        file.LastModified.Returns(new DateTimeOffset(DosEpoch));
        file.CreateReadStream().Returns(static _ => throw new IOException("locked"));
        var inner = Substitute.For<IFileProvider>();
        inner.GetFileInfo("/index.html").Returns(file);

        var served = new ContentValidatedFileProvider(inner).GetFileInfo("/index.html");

        AssertEx.True(ReferenceEquals(file, served), "A file that cannot be read must be served as the inner file, not fail the request.");
    }

    [Test]
    public async Task Fallback_ServesTheShellWithItsContentETag()
    {
        using var root = new TempWebRoot("<p>shell</p>");
        using var host = await StartAsync(root.Files);
        using var client = host.GetTestClient();

        using var direct = await client.GetAsync("/index.html");
        using var fallback = await client.GetAsync("/some/client/route");

        AssertEx.Equal("text/html", fallback.Content.Headers.ContentType?.MediaType);
        AssertEx.Equal(direct.Headers.ETag, fallback.Headers.ETag);
    }

    [Test]
    [Arguments("/index.html")]
    [Arguments("/settings/models")]
    public async Task DateOnlyValidator_OnTheShell_IsIgnored_WhileItsETagStillRevalidates(string path)
    {
        using var root = new TempWebRoot(ShellHashedBefore1980);
        using var host = await StartAsync(root.Files);
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddDays(-1);
        using var dateOnly = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.OK, dateOnly.StatusCode);

        using var tagged = new HttpRequestMessage(HttpMethod.Get, path);
        tagged.Headers.IfNoneMatch.Add(dateOnly.Headers.ETag!);
        tagged.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddDays(-1);
        using var conditional = await client.SendAsync(tagged);
        AssertEx.Equal(HttpStatusCode.NotModified, conditional.StatusCode);
    }

    [Test]
    public async Task UpgradedClient_HoldingTheOldBuilds1980Date_AndNoETag_GetsTheNewShell()
    {
        // Precondition: the new shell's content date folds below the 1980 date the old build sent, the case a date compare gets wrong.
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ShellHashedBefore1980));
        var contentDate = DateTimeOffset.UnixEpoch.AddSeconds(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) & int.MaxValue);
        AssertEx.True(contentDate < new DateTimeOffset(DosEpoch), $"The fixture shell must hash below 1980, got {contentDate:O}.");

        using var root = new TempWebRoot(ShellHashedBefore1980);
        using var host = await StartAsync(root.Files);
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/index.html");
        request.Headers.IfModifiedSince = new DateTimeOffset(DosEpoch);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "shell 16");
    }

    [Test]
    public async Task HashedAsset_KeepsDateValidation()
    {
        using var root = new TempWebRoot("<p>shell</p>");
        using var host = await StartAsync(root.Files);
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/assets/index-AAAAAAAA.js");
        request.Headers.IfModifiedSince = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }

    [Test]
    public async Task DateFormIfRange_OnTheShell_ServesTheWholeFile()
    {
        using var root = new TempWebRoot("<p>shell</p>");
        using var host = await StartAsync(root.Files);
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/index.html");
        request.Headers.Range = new RangeHeaderValue(0, 3);
        request.Headers.IfRange = new RangeConditionHeaderValue(DateTimeOffset.UtcNow.AddDays(-1));
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Null(response.Content.Headers.ContentRange);
        AssertEx.Equal("<p>shell</p>", await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task EntityTagIfRange_OnTheShell_StillServesTheRange()
    {
        using var root = new TempWebRoot("<p>shell</p>");
        using var host = await StartAsync(root.Files);
        using var client = host.GetTestClient();
        using var first = await client.GetAsync("/index.html");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/index.html");
        request.Headers.Range = new RangeHeaderValue(0, 3);
        request.Headers.IfRange = new RangeConditionHeaderValue(first.Headers.ETag!);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        AssertEx.Equal("<p>s", await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task DateFormIfRange_OnAHashedAsset_IsLeftToTheFramework()
    {
        using var root = new TempWebRoot("<p>shell</p>");
        using var host = await StartAsync(root.Files);
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/assets/index-AAAAAAAA.js");
        request.Headers.Range = new RangeHeaderValue(0, 3);
        request.Headers.IfRange = new RangeConditionHeaderValue(DateTimeOffset.UtcNow.AddDays(-1));
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        AssertEx.Equal("cons", await response.Content.ReadAsStringAsync());
    }

    private static Task<IHost> StartAsync(IFileProvider files) =>
        new HostBuilder().ConfigureWebHost(builder =>
        {
            builder.UseTestServer();
            builder.ConfigureServices(static services => services.AddRouting());
            builder.Configure(app =>
            {
                var spaFiles = SpaStaticFileOptions.Create(files);
                app.UseContentValidatedStaticFiles(spaFiles);
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapFallbackToFile("index.html", spaFiles));
            });
        }).StartAsync();

    private sealed class TempWebRoot : IDisposable
    {
        public TempWebRoot(string shell, string assetPage = "<p>page</p>")
        {
            Path = Directory.CreateTempSubdirectory("xe-spa-webroot-").FullName;
            Shell = System.IO.Path.Combine(Path, "index.html");
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "assets"));
            Write(Shell, shell);
            Write(System.IO.Path.Combine(Path, "assets", "index-AAAAAAAA.js"), "console.log(1);");
            Write(System.IO.Path.Combine(Path, "assets", "page.html"), assetPage);
            Write(System.IO.Path.Combine(Path, "favicon.png"), "png");
            Write(System.IO.Path.Combine(Path, "manifest.json"), "{}");
            Files = new PhysicalFileProvider(Path);
        }

        public PhysicalFileProvider Files { get; }

        public string Path { get; }

        public string Shell { get; }

        public void Dispose()
        {
            Files.Dispose();
            Directory.Delete(Path, recursive: true);
        }

        private static void Write(string file, string content)
        {
            File.WriteAllText(file, content);
            File.SetLastWriteTimeUtc(file, DosEpoch);
        }
    }
}
