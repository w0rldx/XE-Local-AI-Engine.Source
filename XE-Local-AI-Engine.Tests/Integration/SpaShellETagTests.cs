namespace XE_Local_AI_Engine.Tests.Integration;

using System.Net;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The real host serves the shell, directly and through the SPA fallback, with one content ETag that revalidates to 304.</summary>
[Category(TestCategories.Integration)]
public sealed class SpaShellETagTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task Shell_HasOneStableETag_AndRevalidatesTo304()
    {
        using var client = Factory.CreateClient();

        using var first = await client.GetAsync("/index.html");
        using var second = await client.GetAsync("/index.html");
        using var fallback = await client.GetAsync("/some/client/route");
        var tag = AssertEx.NotNull(first.Headers.ETag, "The shell carried no ETag.");
        AssertEx.Equal(tag, second.Headers.ETag);
        AssertEx.Equal(tag, fallback.Headers.ETag);
        AssertEx.True(fallback.Headers.CacheControl?.NoCache == true, $"The fallback shell must carry no-cache, got '{fallback.Headers.CacheControl}'.");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.IfNoneMatch.Add(tag);
        using var conditional = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.NotModified, conditional.StatusCode);
    }
}
