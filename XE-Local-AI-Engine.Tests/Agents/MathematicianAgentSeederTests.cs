namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
///     <para>
///         The <c>Compute:Enabled</c> gate is covered in both directions: a disabled node seeds nothing (shipped
///         config is disabled, and the persona's only tool is refused there), and a row seeded while compute was on
///         SURVIVES a later start with it off — the seeder is additive-only and must never delete.
///     </para>
/// </summary>
public sealed class MathematicianAgentSeederTests
{
    [Test]
    public async Task MathematicianAgentSeeder_SeedsRunPythonWithApprovalOn_AndIsIdempotent()
    {
        await using var factory = new TestServerWebAppFactory();
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();
        var seeder = CreateSeeder(scopeFactory, computeEnabled: true);

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

    [Test]
    public async Task MathematicianAgentSeeder_SeedsNothing_WhenComputeIsDisabled()
    {
        await using var factory = new TestServerWebAppFactory();
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();
        var seeder = CreateSeeder(scopeFactory, computeEnabled: false);

        await seeder.StartAsync(CancellationToken.None).ConfigureAwait(false);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();

        var slugs = await store.ListSeededSlugsAsync().ConfigureAwait(false);
        AssertEx.False(slugs.Contains(AgentDefaults.MathematicianAgentSeedSlug),
            "a node with Compute:Enabled=false must not publish an agent whose only tool is refused on every call");

        var definitions = await store.ListAsync().ConfigureAwait(false);
        AssertEx.False(definitions.Any(definition => definition.SeedSlug == AgentDefaults.MathematicianAgentSeedSlug),
            "no Mathematician definition may exist after a disabled-compute start");
    }

    [Test]
    public async Task MathematicianAgentSeeder_KeepsAnAlreadySeededAgent_WhenComputeIsLaterDisabled()
    {
        await using var factory = new TestServerWebAppFactory();
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();

        // Boot 1: compute on — the Mathematician is seeded.
        await CreateSeeder(scopeFactory, computeEnabled: true).StartAsync(CancellationToken.None).ConfigureAwait(false);

        // Boot 2: the operator turned compute off. Seeding is additive-only, so the existing row must survive.
        await CreateSeeder(scopeFactory, computeEnabled: false).StartAsync(CancellationToken.None).ConfigureAwait(false);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();

        var slugs = await store.ListSeededSlugsAsync().ConfigureAwait(false);
        AssertEx.Contains(slugs, AgentDefaults.MathematicianAgentSeedSlug);

        var definitions = await store.ListAsync().ConfigureAwait(false);
        var rows = definitions.Where(definition => definition.SeedSlug == AgentDefaults.MathematicianAgentSeedSlug).ToList();
        AssertEx.Equal(expected: 1, rows.Count);
        AssertEx.Contains(rows[0].AllowedToolNames, ComputeToolDefinition.ToolName);
    }

    private static MathematicianAgentSeeder CreateSeeder(IServiceScopeFactory scopeFactory, bool computeEnabled)
    {
        return new MathematicianAgentSeeder(scopeFactory,
            Options.Create(new ComputeOptions
            {
                Enabled = computeEnabled
            }),
            NullLogger<MathematicianAgentSeeder>.Instance);
    }
}
