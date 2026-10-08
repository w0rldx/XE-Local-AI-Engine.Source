namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The SPA shell must always revalidate, or a browser keeps an upgraded-away shell pointing at deleted entry chunks.</summary>
[Category(TestCategories.Integration)]
public sealed class SpaShellCacheHeaderTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    [Arguments("/")]
    [Arguments("/index.html")]
    [Arguments("/settings/models")]
    public async Task Shell_IsServedWithNoCache(string path)
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync(path);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        AssertEx.True(response.Headers.CacheControl?.NoCache == true, $"{path} must carry Cache-Control: no-cache, got '{response.Headers.CacheControl}'.");
    }
}
