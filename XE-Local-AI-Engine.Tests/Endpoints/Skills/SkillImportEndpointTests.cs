namespace XE_Local_AI_Engine.Tests.Endpoints.Skills;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.Agents;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The third-party import routes (<c>skills/import/preview</c>, <c>skills/import</c>) and the bundled-resource
///     routes, over the full DI graph. The invariants asserted here are the feature's security boundary: a preview
///     writes nothing, an unacknowledged import writes nothing, an imported skill lands disabled with Imported
///     provenance, a resource list carries no content, and a report token is single-use.
/// </summary>
public sealed class SkillImportEndpointTests
{
    private const string ListRoute = "/api/local/v1/skills";
    private const string PreviewRoute = "/api/local/v1/skills/import/preview";
    private const string ImportRoute = "/api/local/v1/skills/import";

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    /// <summary>
    ///     A skill name only this test will ever use. The class shares one host and one library, so a fixed name would
    ///     let a concurrent sibling's row turn this test's import into a Replaced (or a preview into a conflict).
    /// </summary>
    private static string SkillName()
    {
        return $"pdf-tools-{Guid.NewGuid():N}";
    }

    [Test]
    public async Task Preview_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var source = new StringContent("Paste");
        using var markdown = new StringContent(SkillImportFixtures.SkillMarkdown(SkillName()));
        using var content = new MultipartFormDataContent
        {
            {
                source, "source"
            },
            {
                markdown, "markdown"
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, PreviewRoute)
        {
            Content = content
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Import_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ImportRoute)
        {
            Content = JsonContent.Create(new
            {
                token = Guid.NewGuid(),
                skillNames = new[]
                {
                    SkillName()
                },
                acknowledged = true
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ListResources_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ListRoute}/{Guid.NewGuid()}/resources");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Preview_ReportsTheSkillAndWritesNothing()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var name = SkillName();
        var preview = await PreviewArchiveAsync(factory, client, name).ConfigureAwait(false);
        var skill = preview.GetProperty("skills")[0];

        AssertEx.Equal(name, skill.GetProperty("name").GetString());
        AssertEx.True(skill.GetProperty("canImport").GetBoolean(), "A well-formed skill previews as importable.");
        AssertEx.Equal(expected: 1, skill.GetProperty("resources").GetArrayLength());

        // The whole point of phase 1: the library never saw this skill.
        AssertEx.Equal(expected: 0, (await ListSkillsNamedAsync(factory, client, name).ConfigureAwait(false)).Count);
    }

    [Test]
    public async Task Preview_ReportsResourceNamesWithoutContent()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var preview = await PreviewArchiveAsync(factory, client, SkillName()).ConfigureAwait(false);
        var resource = preview.GetProperty("skills")[0].GetProperty("resources")[0];

        AssertEx.Equal("references/FAQ.md", resource.GetProperty("name").GetString());
        AssertEx.False(resource.TryGetProperty("content", out _), "The report must not carry resource payloads.");
    }

    [Test]
    public async Task Import_WithoutAcknowledgement_ReturnsBadRequestAndWritesNothing()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var name = SkillName();
        var preview = await PreviewArchiveAsync(factory, client, name).ConfigureAwait(false);

        using var response = await CommitAsync(factory,
                client,
                preview.GetProperty("token").GetGuid(),
                name,
                acknowledged: false)
            .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, (await ListSkillsNamedAsync(factory, client, name).ConfigureAwait(false)).Count);
    }

    [Test]
    public async Task Import_WhenAcknowledged_LandsSkillDisabledWithImportedProvenance()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var name = SkillName();
        var preview = await PreviewArchiveAsync(factory, client, name).ConfigureAwait(false);

        using var commitResponse = await CommitAsync(factory,
                client,
                preview.GetProperty("token").GetGuid(),
                name,
                acknowledged: true)
            .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, commitResponse.StatusCode);

        var commitPayload = await commitResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var commitDocument = JsonDocument.Parse(commitPayload);
        var outcome = commitDocument.RootElement.GetProperty("outcomes")[0];
        AssertEx.Equal(name, outcome.GetProperty("name").GetString());
        AssertEx.Equal("Imported", outcome.GetProperty("status").GetString());

        var rows = await ListSkillsNamedAsync(factory, client, name).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, rows.Count);

        var summary = rows[0];
        AssertEx.False(summary.GetProperty("enabled").GetBoolean(),
            "An imported skill lands disabled — the resolver only resolves enabled skills.");
        AssertEx.Equal("Imported", summary.GetProperty("origin").GetString());
        AssertEx.Equal("upload", summary.GetProperty("sourceUri").GetString());

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{ListRoute}/{summary.GetProperty("id").GetGuid()}");
        factory.AddNodeBearerToken(getRequest);
        using var getResponse = await client.SendAsync(getRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var getPayload = await getResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var getDocument = JsonDocument.Parse(getPayload);
        var detail = getDocument.RootElement;
        AssertEx.Equal(expected: 1, detail.GetProperty("resourceCount").GetInt32());
        AssertEx.Equal("MIT", detail.GetProperty("license").GetString());
        AssertEx.True(detail.GetProperty("importedAtUtc").GetInt64() > 0, "An imported row is stamped with its import time.");
    }

    [Test]
    public async Task Import_WhenTokenUnknown_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var name = SkillName();
        using var response = await CommitAsync(factory, client, Guid.NewGuid(), name, acknowledged: true).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, (await ListSkillsNamedAsync(factory, client, name).ConfigureAwait(false)).Count);
    }

    [Test]
    public async Task Import_WhenTokenReplayed_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var name = SkillName();
        var preview = await PreviewArchiveAsync(factory, client, name).ConfigureAwait(false);
        var token = preview.GetProperty("token").GetGuid();

        using (var first = await CommitAsync(factory, client, token, name, acknowledged: true).ConfigureAwait(false))
        {
            AssertEx.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var replay = await CommitAsync(factory, client, token, name, acknowledged: true).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        AssertEx.Equal(expected: 1,
            (await ListSkillsNamedAsync(factory, client, name).ConfigureAwait(false)).Count);
    }

    [Test]
    public async Task Update_WhenFrontmatterIsEchoedBack_PreservesItAndKeepsImportedProvenance()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var name = SkillName();
        var skillId = await ImportSkillAsync(factory, client, name).ConfigureAwait(false);

        // The store writes the frontmatter column from the input unconditionally, so an update that did not carry the
        // fields back would erase them. Provenance is promote-only, so the same edit must NOT launder the row to Local.
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{ListRoute}/{skillId}")
        {
            Content = JsonContent.Create(new
            {
                name,
                description = "Extract text from PDFs.",
                body = "# PDF tools\n\nEdited body.",
                enabled = true,
                license = "MIT"
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        AssertEx.Equal("MIT", document.RootElement.GetProperty("license").GetString());
        AssertEx.Equal("Imported", document.RootElement.GetProperty("origin").GetString());
    }

    [Test]
    public async Task ListResources_OmitsContent()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var skillId = await ImportSkillAsync(factory, client, SkillName()).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ListRoute}/{skillId}/resources");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var items = document.RootElement.GetProperty("items");

        AssertEx.Equal(expected: 1, items.GetArrayLength());
        AssertEx.Equal("references/FAQ.md", items[0].GetProperty("name").GetString());
        AssertEx.False(items[0].TryGetProperty("content", out _), "The resource list must omit content.");
    }

    [Test]
    public async Task ListResources_WhenSkillMissing_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ListRoute}/{Guid.NewGuid()}/resources");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task GetResource_WhenNameCarriesASlash_ReturnsContent()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var skillId = await ImportSkillAsync(factory, client, SkillName()).ConfigureAwait(false);

        // The client escapes the whole name into one segment; Kestrel leaves %2F encoded, so the endpoint decodes it.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{ListRoute}/{skillId}/resources/{Uri.EscapeDataString("references/FAQ.md")}");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        AssertEx.Equal("references/FAQ.md", document.RootElement.GetProperty("name").GetString());
        AssertEx.Equal("Frequently asked.", document.RootElement.GetProperty("content").GetString());
    }

    [Test]
    public async Task GetResource_WhenNameIsInvalid_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var skillId = await ImportSkillAsync(factory, client, SkillName()).ConfigureAwait(false);

        // Decodes to "../../etc/passwd": the charset guard runs AFTER the decode, so the traversal is what is rejected.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{ListRoute}/{skillId}/resources/{Uri.EscapeDataString("../../etc/passwd")}");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task GetResource_WhenNameIsUnknown_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var skillId = await ImportSkillAsync(factory, client, SkillName()).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ListRoute}/{skillId}/resources/absent.md");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Preview_WhenSourceIsPasteWithoutMarkdown_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var source = new StringContent("Paste");
        using var content = new MultipartFormDataContent
        {
            {
                source, "source"
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, PreviewRoute)
        {
            Content = content
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Preview_WhenPasted_ReportsAnInstructionsOnlySkill()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var name = $"pasted-skill-{Guid.NewGuid():N}";

        using var source = new StringContent("Paste");
        using var markdown = new StringContent(SkillImportFixtures.SkillMarkdown(name));
        using var content = new MultipartFormDataContent
        {
            {
                source, "source"
            },
            {
                markdown, "markdown"
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, PreviewRoute)
        {
            Content = content
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var skill = document.RootElement.GetProperty("skills")[0];
        AssertEx.Equal(name, skill.GetProperty("name").GetString());
        AssertEx.Equal(expected: 0, skill.GetProperty("resources").GetArrayLength());
    }

    /// <summary>An archive holding one skill with one bundled file, used by every write-path test here.</summary>
    private static byte[] BuildArchive(string name)
    {
        return SkillImportFixtures.Zip(zip =>
        {
            zip.AddText($"{name}/SKILL.md",
                $"---\nname: {name}\ndescription: Extract text from PDFs.\nlicense: MIT\n---\n\n# PDF tools\n\nBody line.\n");
            zip.AddText($"{name}/references/FAQ.md", "Frequently asked.");
        });
    }

    /// <summary>Previews <see cref="BuildArchive" /> and returns the parsed report. The caller owns nothing to dispose.</summary>
    private static async Task<JsonElement> PreviewArchiveAsync(TestServerWebAppFactory factory, HttpClient client, string name)
    {
        using var file = new ByteArrayContent(BuildArchive(name));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        using var source = new StringContent("Upload");
        using var content = new MultipartFormDataContent
        {
            {
                source, "source"
            },
            {
                file, "file", "skills.zip"
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, PreviewRoute)
        {
            Content = content
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<JsonElement>(payload);
    }

    private static async Task<HttpResponseMessage> CommitAsync(TestServerWebAppFactory factory,
        HttpClient client,
        Guid token,
        string name,
        bool acknowledged)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ImportRoute)
        {
            Content = JsonContent.Create(new
            {
                token,
                skillNames = new[]
                {
                    name
                },
                acknowledged
            })
        };
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    /// <summary>Runs both phases and returns the id of the skill that landed.</summary>
    private static async Task<Guid> ImportSkillAsync(TestServerWebAppFactory factory, HttpClient client, string name)
    {
        var preview = await PreviewArchiveAsync(factory, client, name).ConfigureAwait(false);
        using var response = await CommitAsync(factory, client, preview.GetProperty("token").GetGuid(), name, acknowledged: true)
            .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = await ListSkillsNamedAsync(factory, client, name).ConfigureAwait(false);
        return rows[0].GetProperty("id").GetGuid();
    }

    /// <summary>
    ///     The library rows carrying <paramref name="name" />. The class shares one library with every sibling test, so
    ///     the absolute list length is not something any test here may assert on.
    /// </summary>
    private static async Task<IReadOnlyList<JsonElement>> ListSkillsNamedAsync(TestServerWebAppFactory factory, HttpClient client, string name)
    {
        var items = await ListSkillsAsync(factory, client).ConfigureAwait(false);
        return items.EnumerateArray()
                    .Where(item => string.Equals(item.GetProperty("name").GetString(), name, StringComparison.Ordinal))
                    .ToList();
    }

    private static async Task<JsonElement> ListSkillsAsync(TestServerWebAppFactory factory, HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<JsonElement>(payload).GetProperty("items");
    }
}
