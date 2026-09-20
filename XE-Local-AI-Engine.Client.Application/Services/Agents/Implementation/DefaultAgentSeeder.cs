namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Idempotent startup task that seeds ONE "Default Assistant" agent definition, so a mode-off send resolves
///     through a real, uniformly-selectable definition that reproduces plain chat exactly.
/// </summary>
/// <remarks>
///     Its instructions ARE the embedded chat prompt the default send path reads, so an unedited row yields a
///     byte-identical config hash, and the resolver grants this slug the full capability-gated offer rather than the
///     intersected set. Seeding runs only when the slug is absent from
///     <see cref="IAgentDefinitionStore.ListSeededSlugsAsync" />, so re-runs never duplicate it and a deleted row is
///     re-seeded next startup. Best-effort and model-free: a node must start even when seeding fails.
/// </remarks>
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

            var seededSlugs = await store.ListSeededSlugsAsync(cancellationToken);
            if (seededSlugs.Contains(AgentDefaults.DefaultAgentSeedSlug))
            {
                // The Default Assistant already exists — nothing to seed (idempotent).
                return;
            }

            var input = await BuildSeedInputAsync(cancellationToken);
            var seeded = await store.AddSeededAsync(input, AgentDefaults.DefaultAgentSeedSlug, cancellationToken);

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
            // Best-effort: a node must start even if the seed fails. With no row the stream service uses the embedded
            // prompt, the full offer and the client label, and the next startup re-attempts.
            _logger.LogWarning(ex, "Default Assistant seeding failed at startup; the default agent definition may be missing until the next start.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     The seed input for the Default Assistant: the embedded chat prompt as its instructions, a single-agent
    ///     kind, no pinned model or reasoning, an empty allowed-tool set and the playbook disabled.
    /// </summary>
    /// <remarks>
    ///     The embedded prompt keeps an unedited row byte-identical to the default send, and the empty allowed set
    ///     costs nothing because the resolver grants this slug the full offer regardless.
    /// </remarks>
    private async Task<AgentDefinitionInput> BuildSeedInputAsync(CancellationToken cancellationToken)
    {
        return new AgentDefinitionInput
        {
            Name = AgentDefaults.DefaultAgentName,
            Description = null,
            Instructions = await LoadEmbeddedInstructionsAsync(cancellationToken),
            ModelProfile = null,
            ReasoningEffort = null,
            Kind = AgentDefinitionKind.Single,
            AllowedToolNames = [],
            ToolApprovals = new Dictionary<string, bool>(StringComparer.Ordinal),
            OrchestrationTopologyJson = null
        };
    }

    /// <summary>
    ///     Reads the embedded chat prompt from <see cref="LocalChatAgentOptions.InstructionsResource" /> — the SAME
    ///     resource the default send path loads — so the seeded instructions never drift from today's prompt.
    /// </summary>
    private async Task<string> LoadEmbeddedInstructionsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.InstructionsResource))
        {
            throw new InvalidOperationException($"{nameof(LocalChatAgentOptions.InstructionsResource)} must be configured.");
        }

        var assembly = typeof(LocalChatAgentOptions).Assembly;
        await using var stream = assembly.GetManifestResourceStream(_options.InstructionsResource)
                                 ?? throw new InvalidOperationException($"Embedded instructions resource '{_options.InstructionsResource}' was not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
