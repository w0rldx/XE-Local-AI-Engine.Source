namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Idempotent startup task that seeds ONE "Default Assistant" agent definition (slug
///     <see cref="AgentDefaults.DefaultAgentSeedSlug" />) so a mode-off send resolves through a real, uniformly-selectable
///     definition that reproduces today's chat exactly. Its instructions ARE the embedded chat prompt
///     (<see cref="LocalChatAgentOptions.InstructionsResource" />, the same resource the default send path reads), so an
///     unedited Default Assistant yields the byte-identical config hash; the resolver grants this slug the full
///     capability-gated tool offer (not the intersected set).
///     <para>
///         <b>Idempotent + self-healing.</b> It seeds only when the slug is absent from
///         <see cref="IAgentDefinitionStore.ListSeededSlugsAsync" />, so re-runs never duplicate it. If an operator
///         deletes the seeded row, the next startup re-seeds it by slug.
///     </para>
///     <para>
///         <b>Best-effort + deterministic (no model).</b> A node must still start even if seeding fails (e.g. a transient
///         DB error), so the expected failures are logged and swallowed; the next startup re-attempts once the underlying
///         issue clears. Resolves <see cref="IAgentDefinitionStore" /> inside a hosted scope.
///     </para>
/// </summary>
public sealed class DefaultAgentSeeder : IHostedService
{
    private readonly ILogger<DefaultAgentSeeder> _logger;
    private readonly LocalChatAgentOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;

    public DefaultAgentSeeder(IServiceScopeFactory scopeFactory,
        IOptions<LocalChatAgentOptions> options,
        ILogger<DefaultAgentSeeder> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();

            var seededSlugs = await store.ListSeededSlugsAsync(cancellationToken).ConfigureAwait(false);
            if (seededSlugs.Contains(AgentDefaults.DefaultAgentSeedSlug))
            {
                // The Default Assistant already exists — nothing to seed (idempotent).
                return;
            }

            var input = BuildSeedInput();
            var seeded = await store.AddSeededAsync(input, AgentDefaults.DefaultAgentSeedSlug, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Seeded the Default Assistant agent definition {AgentDefinitionId} (slug {SeedSlug}).",
                seeded.Id,
                AgentDefaults.DefaultAgentSeedSlug);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down before startup finished; nothing to seed.
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or DbUpdateException)
        {
            // Seeding is best-effort: a node must start even if the seed fails. The stream service falls back to the
            // embedded prompt + full offer + client "Default Assistant" label when the row is absent, and the next
            // startup re-attempts once the underlying issue clears.
            _logger.LogWarning(ex, "Default Assistant seeding failed at startup; the default agent definition may be missing until the next start.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     The seed input for the Default Assistant: the embedded chat prompt as its instructions (so an unedited row is
    ///     byte-identical to today's default send), a single-agent kind, no pinned model/reasoning, an empty allowed-tool
    ///     set (the resolver grants this slug the full offer regardless), and the playbook disabled.
    /// </summary>
    private AgentDefinitionInput BuildSeedInput()
    {
        return new AgentDefinitionInput(AgentDefaults.DefaultAgentName,
            null,
            LoadEmbeddedInstructions(),
            null,
            null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            null,
            false);
    }

    /// <summary>
    ///     Reads the embedded chat prompt from <see cref="LocalChatAgentOptions.InstructionsResource" /> — the SAME
    ///     resource the default send path loads — so the seeded instructions never drift from today's prompt.
    /// </summary>
    private string LoadEmbeddedInstructions()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.InstructionsResource);

        var assembly = typeof(LocalChatAgentOptions).Assembly;
        using var stream = assembly.GetManifestResourceStream(_options.InstructionsResource)
                           ?? throw new InvalidOperationException($"Embedded instructions resource '{_options.InstructionsResource}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
