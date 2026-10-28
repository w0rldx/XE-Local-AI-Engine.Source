namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Seeds the definition templates a node ships with, idempotently on their seed slug, so nobody starts from a
///     blank canvas.
/// </summary>
/// <remarks>
///     An UNTOUCHED seeded row follows the shipped seed; an operator's edit never does. Insert-if-absent alone leaves
///     every existing installation on whatever graph it was first seeded with, so a row whose slug matches is
///     compared by GRAPH HASH against what this build knows it shipped, and one that differs is rewritten through
///     the same update the definition PUT uses. Best-effort: a node must start even when seeding fails. See
///     docs/wiki/25-dev-workflows.md ("Seeded templates").
/// </remarks>
public sealed partial class DevWorkflowDefinitionSeeder : IHostedService
{
    private readonly ILogger<DevWorkflowDefinitionSeeder> _logger;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IServiceScopeFactory _scopeFactory;

    public DevWorkflowDefinitionSeeder(IServiceScopeFactory scopeFactory,
        INodeRuntimeSettings runtimeSettings,
        ILogger<DevWorkflowDefinitionSeeder> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Read once at start: a stored change seeds on the next restart.
        if (!await _runtimeSettings.GetDevWorkflowsEnabledAsync(cancellationToken))
        {
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
            await SeedAsync(store, ResearchPlanApprovalSlug, ResearchPlanApprovalName, ResearchPlanApprovalGraph, [], cancellationToken);
            await SeedAsync(store, FeatureDevelopmentSlug, FeatureDevelopmentName, FeatureDevelopmentGraph, FeatureDevelopmentPriorRevisions, cancellationToken);
            await SeedAsync(store, DevIntakeSlug, DevIntakeName, DevIntakeGraph, DevIntakePriorRevisions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down before startup finished; nothing to seed.
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or DbUpdateException)
        {
            _logger.LogWarning(exception, "Development workflow template seeding failed at startup; the templates may be missing until the next start.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private async Task SeedAsync(IDevWorkflowStore store,
        string seedSlug,
        string name,
        string graphJson,
        IReadOnlyList<string> priorRevisions,
        CancellationToken cancellationToken)
    {
        var existing = (await store.ListDefinitionsAsync(includeArchived: true, cancellationToken))
            .FirstOrDefault(definition => string.Equals(definition.SeedSlug, seedSlug, StringComparison.Ordinal));

        // The store hashes a definition's graph bytes at every save, so this answers "is this row the graph this build ships" without decrypting a blob — and it is the graph BYTES on
        // both sides, the right comparison precisely because an untouched seeded row holds a shipped constant verbatim.
        if (existing is not null && string.Equals(existing.GraphHash, GraphHash(graphJson), StringComparison.Ordinal))
        {
            return;
        }

        // Untouched is "this row is a graph WE shipped", not "this row has never been written": reading it off the version would let the seeder's own upgrade make every later template
        // change look like an operator's edit. Matching content against the published revisions still cannot mistake an operator's graph for ours — they would have typed it byte for byte.
        if (existing is { } row && (row.Archived || !priorRevisions.Any(revision => string.Equals(row.GraphHash, GraphHash(revision), StringComparison.Ordinal))))
        {
            _logger.LogInformation("The {Name} workflow definition {DefinitionId} (slug {SeedSlug}) differs from the template this build ships and from every "
                                   + "revision it knows it published, so it was left as it is: it has been edited or deleted since it was seeded.",
                name,
                row.Id,
                seedSlug);
            return;
        }

        var graph = DevWorkflowGraph.Parse(graphJson);
        if (existing is null)
        {
            var seeded = await store.CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand
                {
                    DefinitionId = Guid.NewGuid(),
                    Name = name,
                    GraphJson = graphJson,
                    NodeCount = graph.Nodes.Count,
                    Source = DevWorkflowDefinitionSource.Seeded,
                    SeedSlug = seedSlug
                },
                cancellationToken);
            _logger.LogInformation("Seeded the {Name} workflow definition {DefinitionId} (slug {SeedSlug}).", name, seeded.Id, seedSlug);
            return;
        }

        // The GRAPH only. A name is the operator's to choose — renaming a seeded template is a name-only PUT that leaves the graph one of ours, so it still qualifies for the catch-up,
        // and passing the shipped name here would revert their label as a side effect of a fix they never asked about.
        var upgraded = await store.UpdateDefinitionAsync(new UpdateDevWorkflowDefinitionCommand
            {
                DefinitionId = existing.Id,
                ExpectedVersion = existing.Version,
                Name = null,
                GraphJson = graphJson,
                NodeCount = graph.Nodes.Count
            },
            cancellationToken);
        _logger.LogInformation("Updated the untouched {Name} workflow definition {DefinitionId} (slug {SeedSlug}) to the template this build ships, version {Version}.",
            name,
            upgraded.Id,
            seedSlug,
            upgraded.Version);
    }

    /// <summary>The hash the store writes beside a definition's graph, computed the same way over the same bytes.</summary>
    internal static string GraphHash(string graphJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(graphJson)));
}
