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

    /// <summary>
    ///     The Slice C template (§5.10): research and a plan a human approves, a decomposition that expands into one
    ///     implementation-and-validation subtree per task, and an integration stage that applies nothing until a second
    ///     human gate says so.
    ///     <para>
    ///         Both gates route on <c>decision eq "Approve"</c> and have no other branch, which is what makes a refusal
    ///         end the run rather than continue past it — for the integration gate that is Y3 itself, and the parser
    ///         enforces the same rule structurally.
    ///     </para>
    ///     <para>
    ///         The implementation node declares a timeout because nothing else bounds it: Dev Mode bounds each attempt
    ///         and each review round, but not attempts back to back. Two hours is a slice of a feature, generously.
    ///     </para>
    ///     <para>
    ///         <b>The <c>decompose → join</c> edge is load-bearing for evidence, not only for routing.</b> Upstream
    ///         artifacts resolve back through structural nodes to the nearest PRODUCING ancestors, and a producing TYPE
    ///         stops that walk whether or not it produced anything — so the join's other inbound edge, from the
    ///         unmaterialized <c>validate</c> template node, contributes nothing. This edge is what carries the approved
    ///         plan to <c>verify</c>; removing it leaves the verification agent with an empty inheritance again.
    ///     </para>
    /// </summary>
    internal const string FeatureDevelopmentSlug = "feature-development-v1";

    private const string FeatureDevelopmentName = "Feature Development v1";

    private const string FeatureDevelopmentGraph = $$"""
                                                    {
                                                      "schemaVersion": 1,
                                                      "nodes": [
                                                        {
                                                          "nodeKey": "research",
                                                          "nodeType": "Agent",
                                                          "label": "Research",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionResearchAgentSeedSlug}}",
                                                          "instructions": "Research what this feature touches in this repository: the code that would change, the conventions it follows, and what already exists that you should reuse. Ground every claim in something you actually read. Before you complete this session you MUST call save_artifact exactly once, with name \"research.md\", mediaType \"text/markdown\", kind \"Report\", and the whole write-up as text."
                                                        },
                                                        {
                                                          "nodeKey": "plan",
                                                          "nodeType": "Agent",
                                                          "label": "Plan",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                          "instructions": "Turn the research into a plan a person can approve: what to build, in what order, and what would show it worked. Before you complete this session you MUST call save_artifact exactly once, with name \"plan.md\", mediaType \"text/markdown\", kind \"Report\", and the whole plan as text. The approval gate shows the operator that artifact, so a plan left in findings is a plan nobody can approve."
                                                        },
                                                        {
                                                          "nodeKey": "planapproval",
                                                          "nodeType": "HumanGate",
                                                          "label": "Approve the plan"
                                                        },
                                                        {
                                                          "nodeKey": "decompose",
                                                          "nodeType": "Agent",
                                                          "label": "Decompose",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                          "instructions": "Split the approved plan into independent implementation tasks, each one a slice a coder can finish and a build can judge on its own. Before you complete this session you MUST call save_artifact exactly once, with name \"tasks.json\", mediaType \"application/json\", kind \"Report\", and a JSON array as the text: [{\"id\": \"short-slug\", \"title\": \"what it is\", \"goal\": \"what to implement, in full — this becomes the task's requirements and it is all the coder is told\", \"dependsOn\": [\"another-id\"], \"acceptanceCriteria\": [\"how to tell it is done\"]}]. Ids must be unique, dependsOn may only name ids in this same array, and there must be no dependency cycle. Ten tasks is the ceiling. If the plan needs no implementation work, answer with an empty array rather than inventing one.",
                                                          "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 10 }
                                                        },
                                                        {
                                                          "nodeKey": "implement",
                                                          "nodeType": "DevTask",
                                                          "label": "Implement",
                                                          "nodeTimeoutSeconds": 7200
                                                        },
                                                        {
                                                          "nodeKey": "validate",
                                                          "nodeType": "Tool",
                                                          "label": "Validate",
                                                          "retryTarget": "implement"
                                                        },
                                                        {
                                                          "nodeKey": "join",
                                                          "nodeType": "Join",
                                                          "label": "Every slice implemented"
                                                        },
                                                        {
                                                          "nodeKey": "verify",
                                                          "nodeType": "Agent",
                                                          "label": "Verify",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                          "instructions": "Judge the implemented slices against the approved plan, independently: read what each task produced and say whether the feature is actually there, what is missing, and what you would not sign off. You are the last reader before an operator is asked to let these patches into the repository, so an honest \"not yet\" is worth more than an approval. Before you complete this session you MUST call save_artifact exactly once, with name \"verification.md\", mediaType \"text/markdown\", kind \"Report\", and your whole assessment as text."
                                                        },
                                                        {
                                                          "nodeKey": "integrationapproval",
                                                          "nodeType": "HumanGate",
                                                          "label": "Approve integration"
                                                        },
                                                        {
                                                          "nodeKey": "integrate",
                                                          "nodeType": "Tool",
                                                          "toolMode": "Apply",
                                                          "label": "Apply the approved patches"
                                                        },
                                                        {
                                                          "nodeKey": "fullvalidate",
                                                          "nodeType": "Tool",
                                                          "label": "Validate the integrated result"
                                                        }
                                                      ],
                                                      "edges": [
                                                        { "from": "research", "to": "plan" },
                                                        { "from": "plan", "to": "planapproval" },
                                                        { "from": "planapproval", "to": "decompose", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                        { "from": "decompose", "to": "join" },
                                                        { "from": "implement", "to": "validate" },
                                                        { "from": "validate", "to": "join" },
                                                        { "from": "join", "to": "verify" },
                                                        { "from": "verify", "to": "integrationapproval" },
                                                        { "from": "integrationapproval", "to": "integrate", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                        { "from": "integrate", "to": "fullvalidate" }
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
            var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
            await SeedAsync(store, ResearchPlanApprovalSlug, ResearchPlanApprovalName, ResearchPlanApprovalGraph, cancellationToken).ConfigureAwait(false);
            await SeedAsync(store, FeatureDevelopmentSlug, FeatureDevelopmentName, FeatureDevelopmentGraph, cancellationToken).ConfigureAwait(false);
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
