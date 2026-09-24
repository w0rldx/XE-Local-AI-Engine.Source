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
[Category(TestCategories.Integration)]
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
        await seeder.StartAsync(CancellationToken.None);
        await seeder.StartAsync(CancellationToken.None);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        var slugs = await store.ListSeededSlugsAsync();
        AssertEx.Contains(slugs, AgentDefaults.WorkSessionGeneralAgentSeedSlug);
        AssertEx.Contains(slugs, AgentDefaults.WorkSessionResearchAgentSeedSlug);

        var definitions = await store.ListAsync();
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
        await seeder.StartAsync(CancellationToken.None);

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
            var general = (await store.ListAsync()).Single(definition => definition.SeedSlug == AgentDefaults.WorkSessionGeneralAgentSeedSlug);
            AssertEx.True(await store.DeleteAsync(general.Id));
        }

        await seeder.StartAsync(CancellationToken.None);

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
            AssertEx.Contains(await store.ListSeededSlugsAsync(), AgentDefaults.WorkSessionGeneralAgentSeedSlug);
        }
    }

    [Test]
    public async Task GeneralPersona_CarriesTheStateToolsAskUserAndTheClock()
    {
        var definition = await ReadSeededAsync(Host.Factory, AgentDefaults.WorkSessionGeneralAgentSeedSlug);

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
        AssertEx.Contains(definition.AllowedToolNames, "GetCurrentTime");
        AssertEx.Equal(expected: 6, definition.AllowedToolNames.Count, "The general persona gets no knowledge-base tools.");
    }

    [Test]
    public async Task ResearchPersona_AddsTheKnowledgeBaseReads()
    {
        var definition = await ReadSeededAsync(Host.Factory, AgentDefaults.WorkSessionResearchAgentSeedSlug);

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
            var definition = await ReadSeededAsync(Host.Factory, slug);
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

    /// <summary>
    ///     The upgrade path. Seeding is additive-only and returns early on an existing slug, so a database written
    ///     before the spelling was fixed would keep <c>get_current_time</c> — a name no tool carries — and its two
    ///     personas would silently never get the clock tool again. The seeder repairs that row in place.
    ///     <para>
    ///         Everything else on the row has to survive it: the repair rebuilds from the STORED record, because an
    ///         operator may have edited this persona and re-seeding theirs would be data loss wearing a fix's clothes.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Seeder_OnADatabaseSeededUnderTheOldClockName_RenamesItAndLeavesTheRestAlone()
    {
        // Private host: it writes and then rewrites a seeded row every other test in this class reads.
        await using var factory = new TestServerWebAppFactory();
        var edited = WorkSessionAgentSeeder.BuildGeneralSeedInput() with
        {
            Description = "Edited by the operator.",
            AllowedToolNames = [.. WorkSessionAgentSeeder.BuildGeneralSeedInput().AllowedToolNames.Select(static name => name == "GetCurrentTime" ? "get_current_time" : name)],
            ToolApprovals = WorkSessionAgentSeeder.BuildGeneralSeedInput()
                                                  .ToolApprovals.ToDictionary(static pair => pair.Key == "GetCurrentTime" ? "get_current_time" : pair.Key,
                                                      static pair => pair.Value,
                                                      StringComparer.Ordinal)
        };

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
            _ = await store.AddSeededAsync(edited, AgentDefaults.WorkSessionGeneralAgentSeedSlug);
        }

        await new WorkSessionAgentSeeder(factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkSessionAgentSeeder>.Instance)
            .StartAsync(CancellationToken.None);

        var repaired = await ReadSeededAsync(factory, AgentDefaults.WorkSessionGeneralAgentSeedSlug);
        AssertEx.Contains(repaired.AllowedToolNames, "GetCurrentTime");
        AssertEx.False(repaired.AllowedToolNames.Contains("get_current_time", StringComparer.Ordinal), "the dead name must be gone, not merely joined by the live one.");
        AssertEx.True(repaired.ToolApprovals.ContainsKey("GetCurrentTime"), "the approval travels with the rename, or the tool arrives needing a click nobody configured.");
        AssertEx.False(repaired.ToolApprovals.ContainsKey("get_current_time"));

        AssertEx.Equal("Edited by the operator.", repaired.Description, "the repair rebuilds from the stored row, so an operator's edit survives it.");
        AssertEx.Equal(edited.AllowedToolNames.Count, repaired.AllowedToolNames.Count, "one name was renamed, none added or dropped.");
        AssertEx.Equal(AgentDefinitionSource.Seeded, repaired.Source, "the row stays seeded, or the next boot writes a duplicate.");

        await using var check = factory.Services.CreateAsyncScope();
        var definitions = await check.ServiceProvider.GetRequiredService<IAgentDefinitionStore>().ListAsync();
        AssertEx.Equal(expected: 1,
            definitions.Count(static definition => definition.SeedSlug == AgentDefaults.WorkSessionGeneralAgentSeedSlug),
            "the repair updates the row it found; it must not add a second.");
    }

    /// <summary>
    ///     A row carrying BOTH spellings. The legacy key must not decide the surviving approval whichever order the two
    ///     were written in — the live name's value is the one an operator configured, and enumeration order is not a
    ///     thing this repair is allowed to depend on.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Seeder_OnARowCarryingBothClockNames_KeepsTheCorrectlyNamedApproval(bool legacyFirst)
    {
        // Private host: it writes and rewrites a seeded row every other test in this class reads.
        await using var factory = new TestServerWebAppFactory();
        var approvals = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (name, requiresApproval) in legacyFirst
                     ? [("get_current_time", true), ("GetCurrentTime", false)]
                     : new[]
                     {
                         ("GetCurrentTime", false),
                         ("get_current_time", true)
                     })
        {
            approvals[name] = requiresApproval;
        }

        var seed = WorkSessionAgentSeeder.BuildGeneralSeedInput();
        var both = seed with
        {
            AllowedToolNames = [.. seed.AllowedToolNames, "get_current_time"],
            ToolApprovals = approvals
        };

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            _ = await scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>()
                           .AddSeededAsync(both, AgentDefaults.WorkSessionGeneralAgentSeedSlug);
        }

        await new WorkSessionAgentSeeder(factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkSessionAgentSeeder>.Instance)
            .StartAsync(CancellationToken.None);

        var repaired = await ReadSeededAsync(factory, AgentDefaults.WorkSessionGeneralAgentSeedSlug);
        AssertEx.False(repaired.ToolApprovals.ContainsKey("get_current_time"));
        AssertEx.False(repaired.ToolApprovals["GetCurrentTime"],
            "the correctly named key already had a value, so the legacy one supplies nothing — whichever was written first.");
        AssertEx.Equal(expected: 1,
            repaired.AllowedToolNames.Count(static name => name == "GetCurrentTime"),
            "the two spellings collapse to one entry, not two.");
    }

    private static async Task<AgentDefinitionRecord> ReadSeededAsync(TestServerWebAppFactory factory, string slug)
    {
        // The host fixture strips every hosted service, so its InitializeAsync ran the seeder once for the whole class
        // and verified the result. Running it again per test would race a sibling into the seed_slug unique index,
        // whose violation the seeder's best-effort contract swallows — leaving this test silently unseeded.
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        return AssertEx.NotNull(await store.GetBySeedSlugAsync(slug), $"The seeder must have created {slug}.");
    }
}
