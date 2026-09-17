namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     One rule over every seeded persona: a tool name it allow-lists has to be a name this node's catalog carries.
///     <para>
///         The agent-send path keeps <c>offered ∩ AllowedToolNames</c>, so a misspelling grants nothing and reports
///         nothing — which is how <c>WorkSessionAgentSeeder</c> shipped the snake_case <c>get_current_time</c> while
///         the registry generates <c>GetCurrentTime</c> from the method name. Asserted across ALL the seeders rather
///         than in each seeder's own suite, because the defect is a class of defect and not a property of one persona.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class SeededAgentToolNameTests
{
    /// <summary>
    ///     Its own host: it runs every seeder against one database and reads back every seeded row, which only holds on
    ///     a database this test owns.
    /// </summary>
    [Test]
    public async Task EverySeededPersona_AllowsOnlyToolNamesTheNodeCatalogCarries()
    {
        await using var factory = new TestServerWebAppFactory();
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();
        foreach (var seeder in Seeders(factory, scopeFactory))
        {
            await seeder.StartAsync(CancellationToken.None);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var known = new HashSet<string>(await scope.ServiceProvider.GetRequiredService<ILocalToolOfferProvider>().GetKnownToolNamesAsync(),
            StringComparer.Ordinal);
        var seeded = (await scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>().ListAsync())
                     .Where(static definition => definition.SeedSlug is not null)
                     .ToList();

        AssertEx.NotEmpty(seeded, "the seeders must have written something, or this asserts over an empty set.");
        foreach (var definition in seeded)
        {
            foreach (var name in definition.AllowedToolNames)
            {
                AssertEx.True(known.Contains(name),
                    $"'{definition.SeedSlug}' allow-lists '{name}', which no tool on this node carries — the offer intersection drops it silently. "
                    + $"Known: {string.Join(", ", known.Order(StringComparer.Ordinal))}.");
            }
        }
    }

    /// <summary>
    ///     The list below is hand-written, so this is what keeps it honest: a new agent seeder that nobody added here
    ///     fails this test instead of quietly going unchecked.
    /// </summary>
    [Test]
    public async Task ThisSuite_CoversEveryAgentSeederInTheApplicationAssembly()
    {
        await using var factory = new TestServerWebAppFactory();
        var covered = Seeders(factory, factory.Services.GetRequiredService<IServiceScopeFactory>()).Select(static seeder => seeder.GetType().Name);
        var declared = typeof(WorkSessionAgentSeeder).Assembly
                                                     .GetTypes()
                                                     .Where(static type => !type.IsAbstract && type.Name.EndsWith("AgentSeeder", StringComparison.Ordinal))
                                                     .Select(static type => type.Name);

        AssertEx.Empty(declared.Except(covered, StringComparer.Ordinal).Order(StringComparer.Ordinal),
            "a new agent seeder has to be added to Seeders() above, or its tool names are never checked.");
    }

    /// <summary>
    ///     Every seeder that allow-lists tool names, built from the CONTAINER's own options rather than from values
    ///     invented here — a seeder reading a different resource or a different feature gate than the node does would
    ///     be seeding something this node never ships.
    /// </summary>
    private static IReadOnlyList<IHostedService> Seeders(TestServerWebAppFactory factory, IServiceScopeFactory scopeFactory) =>
    [
        new DefaultAgentSeeder(scopeFactory, factory.Services.GetRequiredService<IOptions<LocalChatAgentOptions>>(), NullLogger<DefaultAgentSeeder>.Instance),
        new WorkSessionAgentSeeder(scopeFactory, NullLogger<WorkSessionAgentSeeder>.Instance),
        new CoderAgentSeeder(scopeFactory, NullLogger<CoderAgentSeeder>.Instance),

        // Compute is off in the shipped config, and the Mathematician seeder then writes nothing. Forced ON here so
        // its allow-list is actually checked; every other seeder takes the node's own options.
        new MathematicianAgentSeeder(scopeFactory,
            Options.Create(new ComputeOptions
            {
                Enabled = true
            }),
            NullLogger<MathematicianAgentSeeder>.Instance)
    ];
}
