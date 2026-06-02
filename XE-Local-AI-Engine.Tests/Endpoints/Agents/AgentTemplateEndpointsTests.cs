namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Operator-gated starter-pack template endpoints: list (GET) and import (POST). Both require a node bearer token;
///     list returns the curated catalog with provenance flags, and import returns the per-slug outcome buckets.
/// </summary>
public sealed class AgentTemplateEndpointsTests
{
    private const string ListRoute = "/api/local/v1/agents/templates";
    private const string ImportRoute = "/api/local/v1/agents/templates/import";

    [Test]
    public async Task ListTemplates_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ImportTemplates_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ImportRoute)
        {
            Content = JsonContent.Create(new
            {
                slugs = Array.Empty<string>()
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ListTemplates_WhenOperator_ReturnsCatalogWithProvenanceFlags()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var firstSlug = factory.Services.GetRequiredService<IAgentTemplateCatalog>().List()[0].Slug;

        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var items = document.RootElement.GetProperty("items");

        AssertEx.Equal(14, items.GetArrayLength());

        var first = items[0];
        AssertEx.True(!string.IsNullOrWhiteSpace(first.GetProperty("slug").GetString()), "Each item exposes a slug.");
        AssertEx.True(!string.IsNullOrWhiteSpace(first.GetProperty("name").GetString()), "Each item exposes a name.");
        AssertEx.True(first.GetProperty("estimatedPromptTokens").GetInt32() > 0, "Each item exposes a positive token estimate.");
        // Nothing has been imported yet, so every slug reports alreadyImported=false.
        AssertEx.False(first.GetProperty("alreadyImported").GetBoolean(), "An un-imported slug reports alreadyImported=false.");
        AssertEx.True(firstSlug.Length > 0, "The catalog should expose at least one slug.");
    }

    [Test]
    public async Task ImportTemplates_WhenOperator_ImportsCatalogSlugsAndReportsBuckets()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var knownSlug = factory.Services.GetRequiredService<IAgentTemplateCatalog>().List()[0].Slug;

        using var request = new HttpRequestMessage(HttpMethod.Post, ImportRoute)
        {
            Content = JsonContent.Create(new
            {
                slugs = new[] { knownSlug, "not-a-real-slug" }
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        AssertEx.Equal(1, root.GetProperty("imported").GetArrayLength());
        AssertEx.Equal(knownSlug, root.GetProperty("imported")[0].GetString());
        AssertEx.Equal(0, root.GetProperty("skippedExisting").GetArrayLength());
        AssertEx.Equal(1, root.GetProperty("unknown").GetArrayLength());
        AssertEx.Equal("not-a-real-slug", root.GetProperty("unknown")[0].GetString());

        // After import the list endpoint must mark the slug already-imported.
        using var listRequest = new HttpRequestMessage(HttpMethod.Get, ListRoute);
        factory.AddNodeBearerToken(listRequest);
        using var listResponse = await client.SendAsync(listRequest).ConfigureAwait(false);

        var listPayload = await listResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var listDocument = JsonDocument.Parse(listPayload);
        var importedItem = listDocument.RootElement.GetProperty("items")
                                       .EnumerateArray()
                                       .FirstOrDefault(item => string.Equals(item.GetProperty("slug").GetString(), knownSlug, StringComparison.Ordinal));

        AssertEx.Equal(knownSlug, importedItem.GetProperty("slug").GetString());
        AssertEx.True(importedItem.GetProperty("alreadyImported").GetBoolean(),
            "The imported slug must report alreadyImported=true.");
    }
}
