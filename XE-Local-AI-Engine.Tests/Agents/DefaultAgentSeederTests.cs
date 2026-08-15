namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Seeder behavior against the real wired DI graph + encrypted SQLite: the first boot seeds exactly one
///     "Default Assistant" row (instructions = the embedded chat prompt, full provenance), and a second boot is a
///     no-op (idempotent by slug).
/// </summary>
public sealed class DefaultAgentSeederTests
{
    [Test]
    public async Task DefaultAgentSeeder_IsIdempotent()
    {
        await using var factory = new TestServerWebAppFactory();
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();
        var seeder = new DefaultAgentSeeder(scopeFactory, Options.Create(new LocalChatAgentOptions()), NullLogger<DefaultAgentSeeder>.Instance);

        // First boot seeds the Default Assistant; the second boot must NOT duplicate it.
        await seeder.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await seeder.StartAsync(CancellationToken.None).ConfigureAwait(false);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();

        var slugs = await store.ListSeededSlugsAsync().ConfigureAwait(false);
        AssertEx.Contains(slugs, AgentDefaults.DefaultAgentSeedSlug);

        var definitions = await store.ListAsync().ConfigureAwait(false);
        var defaultRows = definitions.Where(definition => definition.SeedSlug == AgentDefaults.DefaultAgentSeedSlug).ToList();
        AssertEx.Equal(expected: 1, defaultRows.Count);

        var seeded = defaultRows[0];
        AssertEx.Equal(AgentDefinitionSource.Seeded, seeded.Source);
        AssertEx.Equal(AgentDefaults.DefaultAgentName, seeded.Name);
        AssertEx.Equal(AgentDefinitionKind.Single, seeded.Kind);
        AssertEx.Empty(seeded.AllowedToolNames);
        AssertEx.Null(seeded.ModelProfile, "The Default Assistant must not pin a model.");
        AssertEx.Null(seeded.ReasoningEffort, "The Default Assistant must not set a reasoning effort.");
        AssertEx.False(seeded.PlaybookEnabled, "The Default Assistant seeds with the playbook disabled.");

        // The instructions ARE the embedded chat prompt (so an unedited Default Assistant is byte-identical to today).
        AssertEx.Equal(LoadEmbeddedChatPrompt(), seeded.Instructions);

        // The id is resolvable by slug — the provider/stream path depends on this projection.
        var bySlug = AssertEx.NotNull(await store.GetBySeedSlugAsync(AgentDefaults.DefaultAgentSeedSlug).ConfigureAwait(false), "The seeded row must be resolvable by slug.");
        AssertEx.Equal(seeded.Id, bySlug.Id);
    }

    private static string LoadEmbeddedChatPrompt()
    {
        var assembly = typeof(LocalChatAgentOptions).Assembly;
        var resourceName = new LocalChatAgentOptions().InstructionsResource;
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Embedded instructions resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
