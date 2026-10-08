namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The repository id is spliced into token-carrying Hub URLs, so a malformed one must be refused before any request
///     is sent: the host is fixed, but '?', '#', '@' and '..' would steer the path or query to another Hub route.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class HuggingFaceRepoIdTests
{
    private static readonly string[] HostileIds = ["owner/name?x=1", "owner/name#frag", "owner/name@main", "../api/whoami", "owner/..", "owner/a/b", "owner", " owner/name"];

    [Test]
    public void IsValid_AcceptsOwnerNameAndRejectsEveryHostileShape()
    {
        AssertEx.True(HuggingFaceRepoId.IsValid("unsloth/Qwen3.5-0.8B-GGUF"));
        AssertEx.True(HuggingFaceRepoId.IsValid("stabilityai/sd_turbo"));
        foreach (var id in HostileIds)
        {
            AssertEx.False(HuggingFaceRepoId.IsValid(id), id);
        }
    }

    [Test]
    public async Task HubAndHeaderReads_SendNoRequestForAHostileId()
    {
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var options = new HuggingFaceOptions();
        var hub = new HfHubClient(http, options, NullLogger<HfHubClient>.Instance, TimeProvider.System);
        var header = new GgufHeaderReader(http, GgufStoreTestInfrastructure.NoTokenStore(), options, NullLogger<GgufHeaderReader>.Instance, TimeProvider.System);

        foreach (var id in HostileIds)
        {
            AssertEx.Null(await hub.GetRepoAsync(id, CancellationToken.None), id);
            AssertEx.Null(await hub.GetRepoAsync(id, "main", CancellationToken.None), id);
            AssertEx.Null((await header.ReadHeaderAsync(id, "model.gguf", "main", CancellationToken.None)).Architecture, id);
        }

        AssertEx.Equal(expected: 0, handler.RequestCount);
    }

    [Test]
    public async Task Download_RefusesAHostileIdBeforeAnyRequest()
    {
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new HfDownloadClient(http, http, GgufStoreTestInfrastructure.NoTokenStore(), Substitute.For<IFreeSpaceProbe>(),
            new HuggingFaceOptions(), NullLogger<HfDownloadClient>.Instance);
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "model.gguf");

        _ = await AssertEx.ThrowsAsync<ArgumentException>(() => client.DownloadAsync("owner/name?x=1", "model.gguf", "main", "model",
            destination, destination + ".part", expectedSizeBytes: 1, expectedSha256: null, progress: null, CancellationToken.None));

        AssertEx.Equal(expected: 0, handler.RequestCount);
        AssertEx.False(Directory.Exists(Path.GetDirectoryName(destination)), "A refused id must not create the destination directory.");
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
