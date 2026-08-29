namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Seeds the definition templates a node ships with, idempotently on their seed slug, so nobody starts from a blank
///     canvas.
///     <para>
///         Each template is parsed through the same validator a run start uses BEFORE it is written: a template that
///         would fail at run start fails at startup instead, where an operator can still be told about it.
///     </para>
///     <para>
///         Best-effort like every other seeder here: a node must start even when seeding fails, and the next startup
///         re-attempts. An ARCHIVED template is never resurrected — a changed template ships under a new slug, so
///         historical runs keep rendering from their own pinned snapshot.
///     </para>
/// </summary>
public sealed class DevWorkflowDefinitionSeeder : IHostedService
{
    /// <summary>
    ///     The Slice A template: strictly linear, no repository needed, ending on the approval that is the point of it.
    ///     Both agent nodes bind by SEED SLUG rather than by id, because the personas they name are themselves seeded
    ///     and their ids differ per node.
    /// </summary>
    internal const string ResearchPlanApprovalSlug = "research-plan-approval";

    private const string ResearchPlanApprovalName = "Research → Plan → Approval";

    private const string ResearchPlanApprovalGraph = $$"""
                                                       {
                                                         "schemaVersion": 1,
                                                         "nodes": [
                                                           {
                                                             "nodeKey": "research",
                                                             "nodeType": "Agent",
                                                             "label": "Research",
                                                             "agentSeedSlug": "{{AgentDefaults.WorkSessionResearchAgentSeedSlug}}",
                                                             "instructions": "Research what the request asks about and record what you find. Ground every claim in something you actually read. Before you complete this session you MUST call save_artifact exactly once, with name \"research.md\", mediaType \"text/markdown\", kind \"Report\", and the whole write-up as text. record_finding is for notes along the way and does NOT satisfy this step: the next node reads your artifact, not your findings."
                                                           },
                                                           {
                                                             "nodeKey": "plan",
                                                             "nodeType": "Agent",
                                                             "label": "Plan",
                                                             "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                             "instructions": "Turn the research into a plan a person can approve: what to do, in what order, and what would show it worked. Before you complete this session you MUST call save_artifact exactly once, with name \"plan.md\", mediaType \"text/markdown\", kind \"Report\", and the whole plan as text. The approval gate shows the operator that artifact, so a plan left in findings is a plan nobody can approve."
                                                           },
                                                           {
                                                             "nodeKey": "approve",
                                                             "nodeType": "HumanGate",
                                                             "label": "Approve the plan"
                                                           }
                                                         ],
                                                         "edges": [
                                                           { "from": "research", "to": "plan" },
                                                           { "from": "plan", "to": "approve" }
                                                         ]
                                                       }
                                                       """;

    private readonly ILogger<DevWorkflowDefinitionSeeder> _logger;
    private readonly DevWorkflowOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;

    public DevWorkflowDefinitionSeeder(IServiceScopeFactory scopeFactory,
        IOptions<DevWorkflowOptions> options,
        ILogger<DevWorkflowDefinitionSeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            await SeedAsync(scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>(),
                    ResearchPlanApprovalSlug,
                    ResearchPlanApprovalName,
                    ResearchPlanApprovalGraph,
                    cancellationToken)
                .ConfigureAwait(false);
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

    private async Task SeedAsync(IDevWorkflowStore store, string seedSlug, string name, string graphJson, CancellationToken cancellationToken)
    {
        var existing = await store.ListDefinitionsAsync(includeArchived: true, cancellationToken).ConfigureAwait(false);
        if (existing.Any(definition => string.Equals(definition.SeedSlug, seedSlug, StringComparison.Ordinal)))
        {
            return;
        }

        var graph = DevWorkflowGraph.Parse(graphJson);
        var seeded = await store.CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand(Guid.NewGuid(),
                                        name,
                                        graphJson,
                                        graph.Nodes.Count,
                                        DevWorkflowDefinitionSource.Seeded,
                                        seedSlug),
                                    cancellationToken)
                                .ConfigureAwait(false);
        _logger.LogInformation("Seeded the {Name} workflow definition {DefinitionId} (slug {SeedSlug}).", name, seeded.Id, seedSlug);
    }
}
