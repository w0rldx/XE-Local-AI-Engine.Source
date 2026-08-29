namespace XE_Local_AI_Engine.Tests.Endpoints.Skills;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Node-wide skill library CRUD endpoints (<c>skills</c> and <c>skills/{id}</c>). Operator-gated; create returns
///     201 with a resolvable Location header; get returns the full skill including the markdown body; list returns the
///     <c>{ items }</c> wrapper and omits the body; update round-trips and validation rejects bad input with 400.
/// </summary>
public sealed class SkillEndpointTests
{
    private const string ListRoute = "/api/local/v1/skills";

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    private static string ItemRoute(Guid skillId)
    {
        return $"/api/local/v1/skills/{skillId}";
    }

    /// <summary>
    ///     A name only this test will ever use. The class shares one host and one library, so a fixed name would let
    ///     concurrent siblings collide on it and would make the library unfilterable.
    /// </summary>
    private static string SkillName()
    {
        return $"code-reviewer-{Guid.NewGuid():N}";
    }

    private static object BuildCreateBody(string name)
    {
        return new
        {
            name,
            description = "Reviews code for correctness and style.",
            body = "# Code reviewer\n\nReview the diff for bugs, then suggest cleanups."
        };
    }

    [Test]
    public async Task List_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Create_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ListRoute)
        {
            Content = JsonContent.Create(BuildCreateBody(SkillName()))
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Get_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ItemRoute(Guid.NewGuid()));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Update_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, ItemRoute(Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
                name = "renamed",
                description = "x",
                body = "y",
                enabled = true
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Delete_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Delete, ItemRoute(Guid.NewGuid()));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Create_WhenValid_Returns201WithResolvableLocationAndBody()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var name = SkillName();

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, ListRoute)
        {
            Content = JsonContent.Create(BuildCreateBody(name))
        };
        factory.AddNodeBearerToken(createRequest);
        using var createResponse = await client.SendAsync(createRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        AssertEx.NotNull(createResponse.Headers.Location);

        var createdPayload = await createResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var createdDocument = JsonDocument.Parse(createdPayload);
        var created = createdDocument.RootElement;
        AssertEx.Equal(name, created.GetProperty("name").GetString());
        AssertEx.True(created.GetProperty("enabled").GetBoolean(), "A new skill defaults to enabled.");
        AssertEx.Equal(expected: 1, created.GetProperty("version").GetInt32());

        // The Location must resolve to GetSkillEndpoint, proving CreatedAtAsync resolved the target through the
        // NameGenerator and that the GET returns the full skill (including body).
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, createResponse.Headers.Location);
        factory.AddNodeBearerToken(getRequest);
        using var getResponse = await client.SendAsync(getRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var getPayload = await getResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var getDocument = JsonDocument.Parse(getPayload);
        var skill = getDocument.RootElement;
        AssertEx.Equal(name, skill.GetProperty("name").GetString());
        AssertEx.Equal("Reviews code for correctness and style.", skill.GetProperty("description").GetString());
        AssertEx.True(skill.GetProperty("body").GetString()!.Contains("Review the diff", StringComparison.Ordinal),
            "GET returns the decrypted markdown body.");
    }

    [Test]
    public async Task Get_WhenMissing_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ItemRoute(Guid.NewGuid()));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task List_OmitsBodyAndReturnsItemsWrapper()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var name = SkillName();
        await CreateSkillAsync(factory, client, name).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var items = document.RootElement.GetProperty("items");

        // The library is shared with every sibling test, so the row this test created is the only assertable thing.
        var item = items.EnumerateArray()
                        .Single(entry => string.Equals(entry.GetProperty("name").GetString(), name, StringComparison.Ordinal));
        AssertEx.True(item.GetProperty("enabled").GetBoolean(), "List carries the enabled flag.");
        AssertEx.False(item.TryGetProperty("body", out _), "List response must omit the skill body.");
    }

    [Test]
    public async Task Update_WhenValid_RoundTripsAndBumpsVersionOnBodyChange()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var name = SkillName();
        var skillId = await CreateSkillAsync(factory, client, name).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Put, ItemRoute(skillId))
        {
            Content = JsonContent.Create(new
            {
                name,
                description = "Reviews code for correctness and style.",
                body = "# Code reviewer v2\n\nReview the diff carefully, then propose fixes.",
                enabled = true
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        AssertEx.True(root.GetProperty("body").GetString()!.Contains("v2", StringComparison.Ordinal), "Body update round-trips.");
        AssertEx.Equal(expected: 2, root.GetProperty("version").GetInt32());
    }

    [Test]
    public async Task Update_WhenMissing_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        // A unique name: the service validates NOCASE-name uniqueness before it looks the id up, so a name a sibling
        // already holds in the shared library would come back 400 instead of the 404 under test.
        using var request = new HttpRequestMessage(HttpMethod.Put, ItemRoute(Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
                name = SkillName(),
                description = "x",
                body = "y",
                enabled = true
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Create_WhenNameInvalid_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        // Leading dash is rejected by the MAF-safe skill-name regex ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$.
        using var request = new HttpRequestMessage(HttpMethod.Post, ListRoute)
        {
            Content = JsonContent.Create(new
            {
                name = "-bad-name",
                description = "x",
                body = "y"
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Create_WhenBodyBlank_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ListRoute)
        {
            Content = JsonContent.Create(new
            {
                name = SkillName(),
                description = "x",
                body = "   "
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Delete_WhenExisting_ReturnsNoContentThenGetIs404()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var skillId = await CreateSkillAsync(factory, client, SkillName()).ConfigureAwait(false);

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, ItemRoute(skillId));
        factory.AddNodeBearerToken(deleteRequest);
        using var deleteResponse = await client.SendAsync(deleteRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, ItemRoute(skillId));
        factory.AddNodeBearerToken(getRequest);
        using var getResponse = await client.SendAsync(getRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Test]
    public async Task Delete_WhenMissing_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Delete, ItemRoute(Guid.NewGuid()));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Guid> CreateSkillAsync(TestServerWebAppFactory factory, HttpClient client, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ListRoute)
        {
            Content = JsonContent.Create(BuildCreateBody(name))
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("id").GetGuid();
    }
}
