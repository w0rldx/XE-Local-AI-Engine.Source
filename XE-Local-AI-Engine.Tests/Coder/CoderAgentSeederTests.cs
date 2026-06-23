namespace XE_Local_AI_Engine.Tests.Coder;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Seeder behavior against the real wired DI graph + encrypted SQLite: the first boot seeds exactly one
///     "Coder (read-only)" row carrying the three v1 tool names (all approvals false, Seeded provenance), and a second
///     boot is a no-op (idempotent by slug).
/// </summary>
public sealed class CoderAgentSeederTests
{
    [Test]
    public async Task CoderAgentSeeder_SeedsExpectedToolNamesAndSeededSource_AndIsIdempotent()
    {
        await using var factory = new TestingWebAppFactory();
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();
        var seeder = new CoderAgentSeeder(scopeFactory, NullLogger<CoderAgentSeeder>.Instance);

        // First boot seeds the Coder agent; the second boot must NOT duplicate it.
        await seeder.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await seeder.StartAsync(CancellationToken.None).ConfigureAwait(false);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();

        var slugs = await store.ListSeededSlugsAsync().ConfigureAwait(false);
        AssertEx.Contains(slugs, AgentDefaults.CoderAgentSeedSlug);

        var definitions = await store.ListAsync().ConfigureAwait(false);
        var coderRows = definitions.Where(definition => definition.SeedSlug == AgentDefaults.CoderAgentSeedSlug).ToList();
        AssertEx.Equal(expected: 1, coderRows.Count);

        var seeded = coderRows[0];
        AssertEx.Equal(AgentDefinitionSource.Seeded, seeded.Source);
        AssertEx.Equal(AgentDefaults.CoderAgentName, seeded.Name);
        AssertEx.Equal(AgentDefinitionKind.Single, seeded.Kind);

        // Exactly the three v1 tool names (code_query is deferred — not seeded).
        AssertEx.Equal(expected: 3, seeded.AllowedToolNames.Count);
        AssertEx.Contains(seeded.AllowedToolNames, CoderToolDefinition.ListFilesToolName);
        AssertEx.Contains(seeded.AllowedToolNames, CoderToolDefinition.ReadFileToolName);
        AssertEx.Contains(seeded.AllowedToolNames, CoderToolDefinition.SearchTextToolName);

        // Every tool approval is false (decision 7 — read-only, auto-run).
        AssertEx.True(seeded.ToolApprovals.Values.All(approval => !approval),
            "the seeded coder tools must have every approval set to false");

        AssertEx.Null(seeded.ModelProfile, "The Coder agent must not pin a model.");
        AssertEx.False(seeded.PlaybookEnabled, "The Coder agent seeds with the playbook disabled.");
    }
}
