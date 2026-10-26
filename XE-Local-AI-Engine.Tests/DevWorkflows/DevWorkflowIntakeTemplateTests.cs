namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The seeded <c>dev-intake-v1</c> template, run: the classifier's <c>output.kind</c> takes each class to its own
///     endpoint, and the two that build something meet in the shared tail through the <c>approved</c> join.
/// </summary>
/// <remarks>
///     The graph is the shipped constant with each persona's seed slug swapped for the bound id the scripted agent
///     answers to: the personas would need a real repository to resolve. Every node, edge and join policy is shipped.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed partial class DevWorkflowIntakeTemplateTests
{
    private const string BoundAgentId = "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b";

    private static readonly string[] FeaturePath = ["research", "plan", "planapproval"];

    private static readonly string[] BugPath = ["triage", "rootcause", "fixplan", "fixapproval"];

    private static readonly string[] Tail = ["approved", "decompose", "join", "verify", "integrationapproval", "integrate", "fullvalidate"];

    [ClassDataSource<DevWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required DevWorkflowHostFixture Host { get; init; }

    /// <summary>Seeding is idempotent on the slug, and the row it writes is the graph the validator accepts.</summary>
    [Test]
    public async Task TheIntakeTemplateIsSeededOnceAndParses()
    {
        // A host of its own: the assertion counts rows by slug, which a sibling seeding the class host would double.
        await using var harness = new DevWorkflowHarness();

        await SeedAsync(harness);
        await SeedAsync(harness);

        await using var scope = harness.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
        var seeded = (await store.ListDefinitionsAsync(includeArchived: true))
                     .Where(static definition => string.Equals(definition.SeedSlug, DevWorkflowDefinitionSeeder.DevIntakeSlug, StringComparison.Ordinal))
                     .ToList();
        AssertEx.Equal(expected: 1, seeded.Count, "a second startup adds nothing.");
        AssertEx.Equal(DevWorkflowDefinitionSource.Seeded, seeded[0].Source);
        AssertEx.Equal(expected: 19, seeded[0].NodeCount);

        var graph = DevWorkflowGraph.Parse((await store.GetDefinitionAsync(seeded[0].Id)).GraphJson);
        AssertEx.Equal("classify", string.Join(", ", graph.EntryNodeKeys.Where(key => !graph.TemplateKeys.Contains(key))));
    }

    /// <summary>
    ///     A question and an unclear request each end on their own node, and nothing of either build path or the tail runs:
    ///     the run completes with every one of those rows skipped.
    /// </summary>
    [Test]
    [Arguments("question", "answer", "needsinfo")]
    [Arguments("unclear", "needsinfo", "answer")]
    public async Task AReadOnlyClass_EndsOnItsOwnNodeWithTheBuildPathsSkipped(string kind, string endpoint, string otherEndpoint)
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await ClassifyAsync(harness, kind);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, endpoint)).Status);
        await harness.SettleAgentAsync(runId, endpoint);
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId)).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, endpoint)).Status);
        foreach (var key in FeaturePath.Concat(BugPath).Concat(Tail).Append(otherEndpoint))
        {
            AssertEx.Equal(DevWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, key)).Status, $"'{key}' is off the '{kind}' path.");
        }
    }

    /// <summary>
    ///     A feature and a bug each take their own front half to their own approval, and the approval carries the run
    ///     through <c>approved</c> into the shared tail, which stops at the integration gate with nothing applied.
    /// </summary>
    [Test]
    [Arguments("feature")]
    [Arguments("bug")]
    public async Task ABuildClass_TakesItsOwnPathAndStopsAtTheIntegrationGate(string kind)
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await ReachPlanApprovalAsync(harness, kind);
        var taken = kind == "feature" ? FeaturePath : BugPath;
        var notTaken = kind == "feature" ? BugPath : FeaturePath;

        await harness.DecideAsync(runId, taken[^1], DevWorkflowDecisionKind.Approve);
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "approved")).Status, "one approval is all an Any join waits for.");
        _ = await harness.SaveAgentArtifactAsync(runId, "decompose", "tasks.json", "[]");
        await harness.SettleAgentAsync(runId, "decompose");
        await harness.AdvanceThroughToolLaneAsync(runId);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "verify")).Status, "verify is reached on whichever plan was approved.");
        await harness.SettleAgentAsync(runId, "verify");
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Equal(DevWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId)).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, (await harness.ReadNodeRunAsync(runId, "integrationapproval")).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, (await harness.ReadNodeRunAsync(runId, "integrate")).Status, "nothing is applied before the integration gate answers.");
        foreach (var key in notTaken.Append("answer").Append("needsinfo"))
        {
            AssertEx.Equal(DevWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, key)).Status, $"'{key}' is off the '{kind}' path.");
        }
    }

    /// <summary>A rejected feature plan ends the run there: nothing past the gate runs and nothing is applied.</summary>
    [Test]
    public async Task ARejectedPlan_EndsTheRunWithNothingDownstreamRunAndNothingApplied()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await ReachPlanApprovalAsync(harness, "feature");

        await harness.DecideAsync(runId, "planapproval", DevWorkflowDecisionKind.Reject);
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId)).Status, "a rejected plan ends the run.");
        foreach (var key in Tail)
        {
            await AssertNeverRanAsync(harness, runId, key, "it is past the rejected plan");
        }
    }

    /// <summary>A rejected integration on the bug path, after its fix plan was approved, leaves <c>integrate</c> never run.</summary>
    [Test]
    public async Task ARejectedIntegration_OnTheBugPath_NeverRunsIntegrate()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await ReachPlanApprovalAsync(harness, "bug");

        await harness.DecideAsync(runId, "fixapproval", DevWorkflowDecisionKind.Approve);
        _ = await harness.AdvanceUntilQuiescentAsync(runId);
        _ = await harness.SaveAgentArtifactAsync(runId, "decompose", "tasks.json", "[]");
        await harness.SettleAgentAsync(runId, "decompose");
        await harness.AdvanceThroughToolLaneAsync(runId);
        await harness.SettleAgentAsync(runId, "verify");
        _ = await harness.AdvanceUntilQuiescentAsync(runId);
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, (await harness.ReadNodeRunAsync(runId, "integrationapproval")).Status);

        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Reject);
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId)).Status, "a rejected integration ends the run.");
        await AssertNeverRanAsync(harness, runId, "integrate", "nothing is applied once the integration is rejected");
        await AssertNeverRanAsync(harness, runId, "fullvalidate", "it follows the apply that never ran");
    }

    /// <summary>A node run that never started: no start time, and so no status a started run could reach.</summary>
    private static async Task AssertNeverRanAsync(DevWorkflowHarness harness, Guid runId, string key, string because)
    {
        var nodeRun = await harness.ReadNodeRunAsync(runId, key);
        AssertEx.Null(nodeRun.StartedAtUtc, $"'{key}' never ran: {because}, yet it read {nodeRun.Status}.");
        AssertEx.True(nodeRun.Status is DevWorkflowNodeRunStatus.Pending or DevWorkflowNodeRunStatus.Skipped or DevWorkflowNodeRunStatus.Cancelled,
            $"'{key}' read {nodeRun.Status}: {because}.");
    }

    /// <summary>Classifies the run as <paramref name="kind" /> and settles that path's agents up to its waiting plan approval.</summary>
    private static async Task<Guid> ReachPlanApprovalAsync(DevWorkflowHarness harness, string kind)
    {
        var runId = await ClassifyAsync(harness, kind);
        var taken = kind == "feature" ? FeaturePath : BugPath;
        foreach (var agent in taken.SkipLast(1))
        {
            await harness.SettleAgentAsync(runId, agent);
            _ = await harness.AdvanceUntilQuiescentAsync(runId);
        }

        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, (await harness.ReadNodeRunAsync(runId, taken[^1])).Status);
        return runId;
    }

    /// <summary>Starts a run on the shipped graph and hands the classifier's session the given <c>output.kind</c>.</summary>
    private static async Task<Guid> ClassifyAsync(DevWorkflowHarness harness, string kind)
    {
        var (projectId, _) = await harness.SeedDevelopmentProjectAsync();
        var runId = await harness.StartRunAsync(BoundGraph(), "Make the export remember the last folder.", projectId);
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        await harness.RequestAgentCompletionAsync(runId, "classify", $$$"""{"Summary":"Classified.","Output":{"kind":"{{{kind}}}","evidenceQuality":"partial"}}""");
        await harness.SettleAgentAsync(runId, "classify");
        _ = await harness.AdvanceUntilQuiescentAsync(runId);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "classify")).Status);
        return runId;
    }

    private static string BoundGraph() =>
        SeedSlug().Replace(DevWorkflowDefinitionSeeder.DevIntakeGraph, $"\"agentDefinitionId\": \"{BoundAgentId}\"");

    [GeneratedRegex("\"agentSeedSlug\": \"[a-z-]+\"")]
    private static partial Regex SeedSlug();

    private static Task SeedAsync(DevWorkflowHarness harness) =>
        new DevWorkflowDefinitionSeeder(harness.Services.GetRequiredService<IServiceScopeFactory>(),
                harness.Services.GetRequiredService<INodeRuntimeSettings>(),
                harness.Services.GetRequiredService<ILogger<DevWorkflowDefinitionSeeder>>())
            .StartAsync(CancellationToken.None);
}
