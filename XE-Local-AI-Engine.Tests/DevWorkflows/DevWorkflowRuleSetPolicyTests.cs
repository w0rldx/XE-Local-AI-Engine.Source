namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     O6 policy: which scoped rule sets a node run resolves to, what the row records about it, and what reaches the
///     agent's objective.
/// </summary>
public sealed class DevWorkflowRuleSetPolicyTests
{
    private static readonly Guid ProjectId = Guid.Parse("7f2c1a44-1111-4000-8000-000000000001");
    private static readonly Guid OtherProjectId = Guid.Parse("7f2c1a44-2222-4000-8000-000000000002");

    private const string HouseRules = "Never touch production without an approved plan.";

    /// <summary>One agent node, so an assertion is about the policy rather than about routing.</summary>
    private const string SingleAgent = """
                                       {
                                         "schemaVersion": 1,
                                         "nodes": [{ "nodeKey": "research", "nodeType": "Agent", "label": "Research",
                                                     "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" }],
                                         "edges": []
                                       }
                                       """;

    /// <summary>
    ///     Y2's predicate, stated as a table: an EMPTY axis matches everything, a populated one is exact and
    ///     case-insensitive, and BOTH have to match. The last two rows are the ones that would silently ship wrong —
    ///     a rule set scoped to a project a node has none of must not apply, and a scope nothing can parse applies to
    ///     nothing rather than to everything.
    /// </summary>
    [Test]
    [Arguments("""{"projectIds":[],"nodeTypes":[]}""", true, true, "both axes empty matches every node")]
    [Arguments("""{"projectIds":[],"nodeTypes":["Agent"]}""", true, true, "an empty project axis still matches, and the node type is the one named")]
    [Arguments("""{"projectIds":[],"nodeTypes":["agent"]}""", true, true, "node-type membership is case-insensitive")]
    [Arguments("""{"projectIds":[],"nodeTypes":["Tool"]}""", true, false, "a rule set for Tool nodes does not reach an Agent node")]
    [Arguments("""{"projectIds":["7f2c1a44-1111-4000-8000-000000000001"],"nodeTypes":[]}""", true, true, "the node's project is in the axis")]
    [Arguments("""{"projectIds":["7f2c1a44-2222-4000-8000-000000000002"],"nodeTypes":[]}""", true, false, "another project's rule set does not apply")]
    [Arguments("""{"projectIds":["7f2c1a44-1111-4000-8000-000000000001"],"nodeTypes":["Tool"]}""", true, false, "one axis matching is not enough — both must")]
    [Arguments("""{"projectIds":["7f2c1a44-1111-4000-8000-000000000001"],"nodeTypes":[]}""", false, false, "a node with no project cannot satisfy a populated project axis")]
    [Arguments("""{"nodeTypes":["Agent"]}""", true, true, "an axis that is absent altogether reads as empty, which is match-all")]
    [Arguments("not json at all", true, false, "a scope nothing can read applies to nothing, not to everything")]
    public void Resolve_AppliesARuleSetOnlyWhenEveryPopulatedAxisMatches(string scopeJson, bool nodeHasProject, bool expected, string because)
    {
        var ruleSet = Summary("House rules", scopeJson);

        var matched = DevWorkflowRulePolicyResolver.Resolve([ruleSet], nodeHasProject ? ProjectId : null, DevWorkflowNodeType.Agent);

        AssertEx.Equal(expected, matched.Count == 1, because);
    }

    /// <summary>Every match is applied, in the order the store hands them over — which is by name.</summary>
    [Test]
    public void Resolve_KeepsEveryMatchInNameOrderAndComposesTheRecordItWrites()
    {
        var alpha = Summary("Alpha", """{"projectIds":[],"nodeTypes":[]}""");
        var mike = Summary("Mike", """{"projectIds":[],"nodeTypes":["Tool"]}""");
        var zulu = Summary("Zulu", $$"""{"projectIds":["{{ProjectId}}"],"nodeTypes":["Agent"]}""");

        var json = AssertEx.NotNull(DevWorkflowRulePolicyResolver.Compose([alpha, mike, zulu], ProjectId, DevWorkflowNodeType.Agent));
        var recorded = DevWorkflowRulePolicyResolver.Read(json);

        AssertEx.Equal("Alpha, Zulu", string.Join(", ", recorded.Select(entry => entry.Name)), "both matches are injected, in the order they were handed over.");
        AssertEx.Equal(alpha.ContentSha256, recorded[0].ContentSha256, "the record names the exact text that applied, not just the document.");
        AssertEx.Null(DevWorkflowRulePolicyResolver.Compose([mike], ProjectId, DevWorkflowNodeType.Agent),
            "nothing matching writes NULL, not an empty array — an untouched column must not claim a resolution.");
    }

    /// <summary>
    ///     End to end: a rule set scoped to this project's Agent nodes is recorded on the node run and its body reaches
    ///     the objective the agent was actually handed. A rule set for another project is not recorded at all.
    /// </summary>
    [Test]
    public async Task AScopedRuleSet_IsRecordedOnTheNodeRunAndItsBodyReachesTheObjective()
    {
        await using var harness = new DevWorkflowHarness();
        var applies = await harness.CreateRuleSetAsync("House rules", HouseRules, $$"""{"projectIds":["{{ProjectId}}"],"nodeTypes":["Agent"]}""").ConfigureAwait(false);
        _ = await harness.CreateRuleSetAsync("Someone else's rules", "Deploy on Fridays.", $$"""{"projectIds":["{{OtherProjectId}}"],"nodeTypes":[]}""")
                         .ConfigureAwait(false);
        _ = await harness.CreateRuleSetAsync("Disabled rules", "Ignore the tests.", """{"projectIds":[],"nodeTypes":[]}""", enabled: false).ConfigureAwait(false);

        var runId = await harness.StartRunAsync(SingleAgent, developmentProjectId: ProjectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        var recorded = DevWorkflowRulePolicyResolver.Read(nodeRun.PolicyResolutionJson);

        AssertEx.Equal(expected: 1, recorded.Count, "only the rule set whose every populated axis matched is recorded.");
        AssertEx.Equal(applies.Id, recorded[0].Id);
        AssertEx.Equal("House rules", recorded[0].Name);
        AssertEx.Equal(applies.ContentSha256, recorded[0].ContentSha256, "the hash is what lets an audit prove WHICH text applied.");

        var objective = harness.Agent.Objectives.Single();
        AssertEx.Contains(objective, "## Policy: House rules", message: "the rule set is rendered as its own section.");
        AssertEx.Contains(objective, HouseRules, message: "and its body is injected verbatim — a heading alone governs nothing.");
        AssertEx.False(objective.Contains("Deploy on Fridays.", StringComparison.Ordinal), "another project's rule set must not reach this agent.");
        AssertEx.False(objective.Contains("Ignore the tests.", StringComparison.Ordinal), "and neither must a disabled one.");
        AssertEx.True(objective.IndexOf("## Policy:", StringComparison.Ordinal) < objective.IndexOf("## What was asked", StringComparison.Ordinal),
            "policy sits between the node's instructions and what was asked, per §5.6.1a.");
    }

    /// <summary>
    ///     P3.7's gate: the node-run keeps the ids it recorded after the rule set is deleted. DELETE is a hard delete and
    ///     does not 409 on a live run, so the recorded {id, name, contentSha256} is the ONLY thing that keeps the audit
    ///     truthful — and the objective composer skips the missing document rather than failing the dispatch.
    /// </summary>
    [Test]
    public async Task ARecordedResolution_SurvivesDeletingTheRuleSetAndTheObjectiveSkipsTheMissingDocument()
    {
        await using var harness = new DevWorkflowHarness();
        var ruleSet = await harness.CreateRuleSetAsync("House rules", HouseRules, """{"projectIds":[],"nodeTypes":[]}""").ConfigureAwait(false);
        var runId = await harness.StartRunAsync(SingleAgent, developmentProjectId: ProjectId).ConfigureAwait(false);

        await harness.DeleteRuleSetAsync(ruleSet.Id).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        var recorded = DevWorkflowRulePolicyResolver.Read(nodeRun.PolicyResolutionJson);

        AssertEx.Equal(expected: 1, recorded.Count, "the resolution was recorded at materialization and the delete cannot reach back into it.");
        AssertEx.Equal(ruleSet.ContentSha256, recorded[0].ContentSha256);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running,
            nodeRun.Status,
            "a deleted rule set is skipped best-effort: the node still dispatched rather than failing over a document that is gone.");
        AssertEx.False(harness.Agent.Objectives.Single().Contains("## Policy:", StringComparison.Ordinal), "there was no body left to inject.");
    }

    /// <summary>
    ///     A materialized clone resolves for ITSELF, against its own node type — so a rule set scoped to Tool nodes
    ///     reaches the cloned validation node and not the cloned implementation beside it. Without this the children a
    ///     run grows would be the one part of it no policy ever governed.
    /// </summary>
    [Test]
    public async Task AMaterializedClone_RecordsItsOwnResolution()
    {
        await using var harness = new DevWorkflowHarness();
        var toolRules = await harness.CreateRuleSetAsync("Sandbox rules", "Run the fast suite only.", """{"projectIds":[],"nodeTypes":["Tool"]}""")
                                     .ConfigureAwait(false);

        var runId = await harness.StartRunAsync(DevWorkflowGraphs.DecompositionSubtree, developmentProjectId: ProjectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId,
                             "decompose",
                             "tasks.json",
                             """[{ "id": "alpha", "title": "Add the parser", "goal": "Parse the manifest." }]""")
                         .ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var clonedTool = await harness.ReadNodeRunAsync(runId, "validate#alpha").ConfigureAwait(false);
        var clonedAgent = await harness.ReadNodeRunAsync(runId, "implement#alpha").ConfigureAwait(false);

        var onTheTool = DevWorkflowRulePolicyResolver.Read(clonedTool.PolicyResolutionJson);
        AssertEx.Equal(expected: 1, onTheTool.Count, "the cloned Tool node resolved against its OWN node type.");
        AssertEx.Equal(toolRules.Id, onTheTool[0].Id);
        AssertEx.Empty(DevWorkflowRulePolicyResolver.Read(clonedAgent.PolicyResolutionJson), "and the cloned Agent node beside it recorded nothing, because nothing applied.");
    }

    private static DevWorkflowRuleSetSummary Summary(string name, string scopeJson) =>
        new(Guid.NewGuid(),
            name,
            Description: null,
            scopeJson,
            Enabled: true,
            ContentSha256: $"hash-of-{name}",
            Version: 1,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1);
}
