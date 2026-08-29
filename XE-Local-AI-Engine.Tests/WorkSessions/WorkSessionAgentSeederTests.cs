namespace XE_Local_AI_Engine.Tests.WorkSessions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Knowledge.Tools;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The two work-session personas against the real wired graph. Each has to carry the four state tool names in
///     <c>AllowedToolNames</c>: the agent-send path intersects the offer with that list, and the state tools appear only
///     in the profile-opt-in offer, so an agent that does not name them gets none of them.
/// </summary>
public sealed class WorkSessionAgentSeederTests
{
    [ClassDataSource<SeededWorkSessionAgentsFixture>(Shared = SharedType.PerClass)]
    public required SeededWorkSessionAgentsFixture Host { get; init; }

    [Test]
    public async Task Seeder_SeedsBothPersonasOnce_AndIsIdempotent()
    {
        // Private host: it counts the seeded rows of a whole database, which only holds on a database it owns.
        await using var factory = new TestServerWebAppFactory();
        var seeder = new WorkSessionAgentSeeder(factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkSessionAgentSeeder>.Instance);

        // Two boots must not duplicate a row. The fixture strips every hosted service, so the seeder is driven here
        // rather than by host startup.
        await seeder.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await seeder.StartAsync(CancellationToken.None).ConfigureAwait(false);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        var slugs = await store.ListSeededSlugsAsync().ConfigureAwait(false);
        AssertEx.Contains(slugs, AgentDefaults.WorkSessionGeneralAgentSeedSlug);
        AssertEx.Contains(slugs, AgentDefaults.WorkSessionResearchAgentSeedSlug);

        var definitions = await store.ListAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 1, definitions.Count(definition => definition.SeedSlug == AgentDefaults.WorkSessionGeneralAgentSeedSlug));
        AssertEx.Equal(expected: 1, definitions.Count(definition => definition.SeedSlug == AgentDefaults.WorkSessionResearchAgentSeedSlug));
    }

    [Test]
    public async Task Seeder_AfterTheRowIsDeleted_ReSeedsItBySlug()
    {
        // Private host: it deletes a seeded persona, which every other test in this class reads.
        await using var factory = new TestServerWebAppFactory();
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();
        var seeder = new WorkSessionAgentSeeder(scopeFactory, NullLogger<WorkSessionAgentSeeder>.Instance);
        await seeder.StartAsync(CancellationToken.None).ConfigureAwait(false);

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
            var general = (await store.ListAsync().ConfigureAwait(false)).Single(definition => definition.SeedSlug == AgentDefaults.WorkSessionGeneralAgentSeedSlug);
            AssertEx.True(await store.DeleteAsync(general.Id).ConfigureAwait(false));
        }

        await seeder.StartAsync(CancellationToken.None).ConfigureAwait(false);

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
            AssertEx.Contains(await store.ListSeededSlugsAsync().ConfigureAwait(false), AgentDefaults.WorkSessionGeneralAgentSeedSlug);
        }
    }

    [Test]
    public async Task GeneralPersona_CarriesTheStateToolsAskUserAndTheClock()
    {
        var definition = await ReadSeededAsync(Host.Factory, AgentDefaults.WorkSessionGeneralAgentSeedSlug).ConfigureAwait(false);

        AssertEx.Equal(AgentDefaults.WorkSessionGeneralAgentName, definition.Name);
        AssertEx.Equal(AgentDefinitionSource.Seeded, definition.Source);
        AssertEx.Equal(AgentDefinitionKind.Single, definition.Kind);
        AssertEx.Null(definition.ModelProfile, "A work-session persona pins no model.");
        AssertEx.False(definition.PlaybookEnabled);

        foreach (var name in WorkSessionToolDefinitions.ToolNames)
        {
            AssertEx.Contains(definition.AllowedToolNames, name);
        }

        AssertEx.Contains(definition.AllowedToolNames, AskUserTool.ToolName);
        AssertEx.Contains(definition.AllowedToolNames, "get_current_time");
        AssertEx.Equal(expected: 6, definition.AllowedToolNames.Count, "The general persona gets no knowledge-base tools.");
    }

    [Test]
    public async Task ResearchPersona_AddsTheKnowledgeBaseReads()
    {
        var definition = await ReadSeededAsync(Host.Factory, AgentDefaults.WorkSessionResearchAgentSeedSlug).ConfigureAwait(false);

        AssertEx.Contains(definition.AllowedToolNames, SearchKnowledgeBaseToolDefinition.ToolName);
        AssertEx.Contains(definition.AllowedToolNames, ReadDocumentToolDefinition.ToolName);
        AssertEx.Contains(definition.AllowedToolNames, ReadSurroundingChunksToolDefinition.ToolName);
        AssertEx.Equal(expected: 9, definition.AllowedToolNames.Count);
    }

    [Test]
    public async Task BothPersonas_ApproveEveryToolExceptAskUser()
    {
        foreach (var slug in new[]
                 {
                     AgentDefaults.WorkSessionGeneralAgentSeedSlug,
                     AgentDefaults.WorkSessionResearchAgentSeedSlug
                 })
        {
            var definition = await ReadSeededAsync(Host.Factory, slug).ConfigureAwait(false);
            foreach (var (name, requiresApproval) in definition.ToolApprovals)
            {
                var expected = string.Equals(name, AskUserTool.ToolName, StringComparison.Ordinal);
                AssertEx.Equal(expected,
                    requiresApproval,
                    expected
                        ? "ask_user's approval flag is structural: it is what routes the call through the out-of-stream round-trip a human answer needs."
                        : $"{name} must auto-run; a click per recorded finding would make an unattended session unusable.");
            }
        }
    }

    private static async Task<AgentDefinitionRecord> ReadSeededAsync(TestServerWebAppFactory factory, string slug)
    {
        // The host fixture strips every hosted service, so its InitializeAsync ran the seeder once for the whole class
        // and verified the result. Running it again per test would race a sibling into the seed_slug unique index,
        // whose violation the seeder's best-effort contract swallows — leaving this test silently unseeded.
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        return AssertEx.NotNull(await store.GetBySeedSlugAsync(slug).ConfigureAwait(false), $"The seeder must have created {slug}.");
    }
}
