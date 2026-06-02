namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the starter-pack catalog loads the embedded <c>agent-templates.seed.json</c> resource through the real
///     wired DI graph (not just from disk) and that every template carries the required non-empty fields.
/// </summary>
public sealed class AgentTemplateCatalogTests
{
    private const int ExpectedTemplateCount = 14;

    [Test]
    public async Task Catalog_LoadsEmbeddedSeed_HasAllCuratedTemplatesWithRequiredFields()
    {
        await using var factory = new TestingWebAppFactory();
        var catalog = factory.Services.GetRequiredService<IAgentTemplateCatalog>();

        var templates = catalog.List();

        AssertEx.Equal(ExpectedTemplateCount, templates.Count);

        foreach (var template in templates)
        {
            AssertEx.True(!string.IsNullOrWhiteSpace(template.Slug), "Every template must have a non-empty slug.");
            AssertEx.True(!string.IsNullOrWhiteSpace(template.Name), $"Template '{template.Slug}' must have a non-empty name.");
            AssertEx.True(!string.IsNullOrWhiteSpace(template.Instructions), $"Template '{template.Slug}' must have non-empty instructions.");
            AssertEx.True(!string.IsNullOrWhiteSpace(template.Division), $"Template '{template.Slug}' must have a non-empty division.");
            AssertEx.True(template.EstimatedPromptTokens > 0, $"Template '{template.Slug}' must have a positive estimated token count.");
            AssertEx.NotNull(template.OriginalTools);
        }
    }

    [Test]
    public async Task Catalog_TryGet_ReturnsTemplateForKnownSlug_AndNullForUnknown()
    {
        await using var factory = new TestingWebAppFactory();
        var catalog = factory.Services.GetRequiredService<IAgentTemplateCatalog>();

        var knownSlug = catalog.List()[0].Slug;

        var found = AssertEx.NotNull(catalog.TryGet(knownSlug), "A known slug should resolve a template.");
        AssertEx.Equal(knownSlug, found.Slug);

        AssertEx.Null(catalog.TryGet("not-a-real-slug"), "An unknown slug should resolve null.");
    }
}
