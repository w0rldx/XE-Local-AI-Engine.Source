namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Import-service behavior against the real wired DI graph + encrypted SQLite: new slugs create seeded chat
///     personas, already-seeded slugs are skipped (no duplicate), unknown slugs are reported without a write, and a
///     duplicate slug in one request is deduped.
/// </summary>
public sealed class AgentTemplateImportServiceTests
{
    [Test]
    public async Task ImportAsync_WhenNewSlugs_CreatesSeededChatPersonaRows()
    {
        await using var factory = new TestingWebAppFactory();
        using var scope = factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IAgentTemplateCatalog>();
        var importService = scope.ServiceProvider.GetRequiredService<IAgentTemplateImportService>();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();

        var slug = catalog.List()[0].Slug;

        var result = await importService.ImportAsync([slug]).ConfigureAwait(false);

        AssertEx.Contains(result.Imported, slug);
        AssertEx.Empty(result.SkippedExisting);
        AssertEx.Empty(result.Unknown);

        var definitions = await store.ListAsync().ConfigureAwait(false);
        var created = AssertEx.NotNull(definitions.FirstOrDefault(definition => definition.SeedSlug == slug),
            "The imported slug should have produced a stored definition.");

        // Seeded provenance and a plain chat persona: verbatim instructions, no tools, single kind, no model/effort.
        AssertEx.Equal(AgentDefinitionSource.Seeded, created.Source);
        AssertEx.Equal(slug, created.SeedSlug);
        AssertEx.Equal(AgentDefinitionKind.Single, created.Kind);
        AssertEx.Empty(created.AllowedToolNames);
        AssertEx.Empty(created.ToolApprovals);
        AssertEx.Null(created.ModelProfile, "An imported template should not pin a model profile.");
        AssertEx.Null(created.ReasoningEffort, "An imported template should not set a reasoning effort.");
        AssertEx.Null(created.OrchestrationTopologyJson, "A single-kind import should carry no topology.");

        var template = AssertEx.NotNull(catalog.TryGet(slug), "The slug should resolve a catalog template.");
        AssertEx.Equal(template.Instructions, created.Instructions);
        AssertEx.Equal(template.Name, created.Name);
    }

    [Test]
    public async Task ImportAsync_WhenSlugAlreadySeeded_SkipsWithoutDuplicating()
    {
        await using var factory = new TestingWebAppFactory();
        using var scope = factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IAgentTemplateCatalog>();
        var importService = scope.ServiceProvider.GetRequiredService<IAgentTemplateImportService>();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();

        var slug = catalog.List()[0].Slug;

        _ = await importService.ImportAsync([slug]).ConfigureAwait(false);
        var second = await importService.ImportAsync([slug]).ConfigureAwait(false);

        AssertEx.Empty(second.Imported);
        AssertEx.Contains(second.SkippedExisting, slug);
        AssertEx.Empty(second.Unknown);

        var definitions = await store.ListAsync().ConfigureAwait(false);
        var matches = definitions.Count(definition => definition.SeedSlug == slug);
        AssertEx.Equal(expected: 1, matches);
    }

    [Test]
    public async Task ImportAsync_WhenUnknownSlug_ReportsUnknownAndWritesNothing()
    {
        await using var factory = new TestingWebAppFactory();
        using var scope = factory.Services.CreateScope();
        var importService = scope.ServiceProvider.GetRequiredService<IAgentTemplateImportService>();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();

        const string UnknownSlug = "not-a-real-slug";

        var result = await importService.ImportAsync([UnknownSlug]).ConfigureAwait(false);

        AssertEx.Empty(result.Imported);
        AssertEx.Empty(result.SkippedExisting);
        AssertEx.Contains(result.Unknown, UnknownSlug);

        var definitions = await store.ListAsync().ConfigureAwait(false);
        AssertEx.Empty(definitions);
    }

    [Test]
    public async Task ImportAsync_WhenSlugRequestedTwice_DedupesToOneRow()
    {
        await using var factory = new TestingWebAppFactory();
        using var scope = factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IAgentTemplateCatalog>();
        var importService = scope.ServiceProvider.GetRequiredService<IAgentTemplateImportService>();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();

        var slug = catalog.List()[0].Slug;

        var result = await importService.ImportAsync([slug, slug]).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, result.Imported.Count);
        AssertEx.Contains(result.Imported, slug);

        var definitions = await store.ListAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 1, definitions.Count(definition => definition.SeedSlug == slug));
    }
}
