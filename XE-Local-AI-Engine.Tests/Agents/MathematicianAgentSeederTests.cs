namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Seeder behavior against the real wired DI graph + encrypted SQLite: the first boot seeds exactly one
///     "Mathematician" row naming <c>run_python</c> with its approval left ON, and a second boot is a no-op (idempotent
///     by slug). The tool-name assertion is the load-bearing one — <c>run_python</c> is profile-opt-in only, so a seed
///     that stopped naming it would leave the compute tool unreachable with nothing else failing.
/// </summary>
public sealed class MathematicianAgentSeederTests
{
    [Test]
    public async Task MathematicianAgentSeeder_SeedsRunPythonWithApprovalOn_AndIsIdempotent()
    {
        await using var factory = new TestServerWebAppFactory();
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();
        var seeder = new MathematicianAgentSeeder(scopeFactory, NullLogger<MathematicianAgentSeeder>.Instance);

        // First boot seeds the Mathematician; the second boot must NOT duplicate it.
        await seeder.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await seeder.StartAsync(CancellationToken.None).ConfigureAwait(false);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();

        var slugs = await store.ListSeededSlugsAsync().ConfigureAwait(false);
        AssertEx.Contains(slugs, AgentDefaults.MathematicianAgentSeedSlug);

        var definitions = await store.ListAsync().ConfigureAwait(false);
        var rows = definitions.Where(definition => definition.SeedSlug == AgentDefaults.MathematicianAgentSeedSlug).ToList();
        AssertEx.Equal(expected: 1, rows.Count);

        var seeded = rows[0];
        AssertEx.Equal(AgentDefinitionSource.Seeded, seeded.Source);
        AssertEx.Equal(AgentDefaults.MathematicianAgentName, seeded.Name);
        AssertEx.Equal(AgentDefinitionKind.Single, seeded.Kind);

        AssertEx.Equal(expected: 1, seeded.AllowedToolNames.Count);
        AssertEx.Contains(seeded.AllowedToolNames, ComputeToolDefinition.ToolName);

        AssertEx.True(seeded.ToolApprovals[ComputeToolDefinition.ToolName],
            "the seed opts INTO the compute tool, never out of its approval round-trip");

        AssertEx.Null(seeded.ModelProfile, "The Mathematician must not pin a model.");
    }
}
