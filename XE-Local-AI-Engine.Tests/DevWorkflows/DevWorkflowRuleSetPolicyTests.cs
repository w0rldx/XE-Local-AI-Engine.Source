namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
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

        // The project axis on the clone side, both ways. A clone inherits its PRODUCER's project rather than re-deriving
        // one, so a rule set scoped to that project has to reach it and one scoped to another must not — the half that
        // an all-empty scope would have passed without ever being exercised.
        var projectRules = await harness.CreateRuleSetAsync("Project sandbox rules",
                                            "Never run the slow suite here.",
                                            $$"""{"projectIds":["{{ProjectId}}"],"nodeTypes":["Tool"]}""")
                                        .ConfigureAwait(false);
        _ = await harness.CreateRuleSetAsync("Another project's sandbox rules",
                             "Run everything.",
                             $$"""{"projectIds":["{{OtherProjectId}}"],"nodeTypes":["Tool"]}""")
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
        AssertEx.True(onTheTool.All(entry => entry.Body is null), "a Tool node run records which rule sets applied without carrying text nothing will inject.");
        AssertEx.Equal("Project sandbox rules, Sandbox rules",
            string.Join(", ", onTheTool.Select(entry => entry.Name)),
            "the cloned Tool node resolved against its OWN node type and against the project it inherited from its producer — and NOT against another project's rule set.");
        AssertEx.True(onTheTool.Any(entry => entry.Id == toolRules.Id) && onTheTool.Any(entry => entry.Id == projectRules.Id));
        AssertEx.Empty(DevWorkflowRulePolicyResolver.Read(clonedAgent.PolicyResolutionJson), "and the cloned Agent node beside it recorded nothing, because nothing applied.");
    }

    /// <summary>
    ///     The node run is given the text it RECORDED, not the text the rule set holds now. An edit landing between
    ///     materialization and dispatch must not hand the agent one document while the audit permanently names another:
    ///     the hash on the row has to describe what the agent actually read.
    /// </summary>
    [Test]
    public async Task ARuleSetEditedAfterMaterialization_StillInjectsTheTextTheNodeRunRecorded()
    {
        await using var harness = new DevWorkflowHarness();
        var original = await harness.CreateRuleSetAsync("House rules", HouseRules, """{"projectIds":[],"nodeTypes":[]}""").ConfigureAwait(false);

        // The run is STARTED — which is what writes the resolution — and only then is the rule set rewritten.
        var runId = await harness.StartRunAsync(SingleAgent, developmentProjectId: ProjectId).ConfigureAwait(false);
        _ = await harness.UpdateRuleSetAsync(original.Id, original.Version, "House rules", "Deploy straight to production on Fridays.").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        var recorded = DevWorkflowRulePolicyResolver.Read(nodeRun.PolicyResolutionJson);
        var objective = harness.Agent.Objectives.Single();

        AssertEx.Contains(objective, HouseRules, message: "the agent reads the text that applied when the node run was materialized.");
        AssertEx.False(objective.Contains("Deploy straight to production on Fridays.", StringComparison.Ordinal),
            "an edit that landed after materialization must not reach an objective the audit describes with the OLD hash.");
        AssertEx.Equal(original.ContentSha256, recorded[0].ContentSha256, "the recorded hash is unchanged by the edit.");
        AssertEx.Equal(original.ContentSha256,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AssertEx.NotNull(recorded[0].Body)))),
            "and it is the hash OF the injected text: the audit and the objective cannot tell different stories.");
    }

    /// <summary>The same guarantee against a delete: the recorded text is still injected, because the row carries it.</summary>
    [Test]
    public async Task ARuleSetDeletedAfterMaterialization_StillInjectsTheTextTheNodeRunRecorded()
    {
        await using var harness = new DevWorkflowHarness();
        var ruleSet = await harness.CreateRuleSetAsync("House rules", HouseRules, """{"projectIds":[],"nodeTypes":[]}""").ConfigureAwait(false);
        var runId = await harness.StartRunAsync(SingleAgent, developmentProjectId: ProjectId).ConfigureAwait(false);

        await harness.DeleteRuleSetAsync(ruleSet.Id).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        var recorded = DevWorkflowRulePolicyResolver.Read(nodeRun.PolicyResolutionJson);

        AssertEx.Equal(expected: 1, recorded.Count, "P3.7: the node run keeps the ids it recorded, whatever became of the documents.");
        AssertEx.Equal(ruleSet.ContentSha256, recorded[0].ContentSha256, "the resolution was recorded at materialization and the delete cannot reach back into it.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, nodeRun.Status, "and the node dispatched rather than failing over a rule set that is gone.");
        AssertEx.Contains(harness.Agent.Objectives.Single(),
            HouseRules,
            message: "and the policy still governs the node: a deleted document is exactly the case a snapshot exists for.");
    }

    /// <summary>
    ///     Policy text is TRUNCATED, visibly, rather than dropped. Two long rule sets split the room left the way the
    ///     upstream artifacts do, so a long first policy cannot crowd out the one after it, and each says in the
    ///     objective that it was cut — an agent handed half a policy has to be able to tell that the rest exists.
    /// </summary>
    [Test]
    public async Task PolicyTextTooLongForTheObjective_IsTruncatedWithAMarkerRatherThanSilentlyDropped()
    {
        await using var harness = new DevWorkflowHarness();
        _ = await harness.CreateRuleSetAsync("Alpha rules", "ALPHA-HEAD " + new string('a', 4096), """{"projectIds":[],"nodeTypes":[]}""").ConfigureAwait(false);
        _ = await harness.CreateRuleSetAsync("Bravo rules", "BRAVO-HEAD " + new string('b', 4096), """{"projectIds":[],"nodeTypes":[]}""").ConfigureAwait(false);

        var runId = await harness.StartRunAsync(SingleAgent, developmentProjectId: ProjectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var objective = harness.Agent.Objectives.Single();

        AssertEx.True(objective.Length <= DevWorkflowAgentExecutor.MaxObjectiveCharacters,
            $"the objective is {objective.Length} characters, past the {DevWorkflowAgentExecutor.MaxObjectiveCharacters} the work-session layer accepts.");
        AssertEx.Contains(objective, "## Policy: Alpha rules");
        AssertEx.Contains(objective, "## Policy: Bravo rules", message: "the fair share leaves room for the SECOND rule set: a long first one must not crowd it out.");
        AssertEx.Contains(objective, "ALPHA-HEAD", message: "what did fit is the head of the document, not an empty heading.");
        AssertEx.Contains(objective, "BRAVO-HEAD");
        AssertEx.Equal(expected: 2,
            objective.Split("[policy text truncated:", StringSplitOptions.None).Length - 1,
            "both cut policies say so in the objective — a truncation an agent cannot see is one it will treat as the whole rule.");
    }

    /// <summary>
    ///     Only the node types that INJECT policy text carry a copy of it. Every node type still records WHICH rule
    ///     sets applied — a Tool or DevTask row that snapshotted bodies would be storing, encrypting and decrypting a
    ///     document nothing ever reads, on every node-run list.
    /// </summary>
    [Test]
    [Arguments(DevWorkflowNodeType.Agent, true)]
    [Arguments(DevWorkflowNodeType.Tool, false)]
    [Arguments(DevWorkflowNodeType.DevTask, false)]
    [Arguments(DevWorkflowNodeType.HumanGate, false)]
    public void Compose_SnapshotsTheBodyOnlyForNodeTypesThatInjectIt(DevWorkflowNodeType nodeType, bool expectsBody)
    {
        var ruleSet = Summary("House rules", """{"projectIds":[],"nodeTypes":[]}""");

        var recorded = DevWorkflowRulePolicyResolver.Read(DevWorkflowRulePolicyResolver.Compose([ruleSet], ProjectId, nodeType));

        AssertEx.Equal(expected: 1, recorded.Count, "every node type records WHICH rule sets applied, whatever it does with the text.");
        AssertEx.Equal(ruleSet.ContentSha256, recorded[0].ContentSha256, "and names the exact text by hash, which is the audit.");
        AssertEx.Equal(expectsBody, recorded[0].Body is not null, $"a {nodeType} node run must {(expectsBody ? "carry" : "not carry")} the snapshotted text.");
    }

    /// <summary>
    ///     An entry with no snapshotted text injects NOTHING. No such row exists today; the reader stays honest about
    ///     the shape rather than falling back to re-reading the rule set, which is the divergence the snapshot closes.
    /// </summary>
    [Test]
    public void ARecordedEntryWithNoBody_ReadsBackWithANullBodyRatherThanFailing()
    {
        var recorded = DevWorkflowRulePolicyResolver.Read("""[{"id":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","name":"House rules","contentSha256":"content-hash"}]""");

        AssertEx.Equal(expected: 1, recorded.Count);
        AssertEx.Null(recorded[0].Body, "a row written before the body was snapshotted reads as having none, not as having empty text.");
        AssertEx.Equal("content-hash", recorded[0].ContentSha256, "and the audit half of it is untouched.");
    }

    private static DevWorkflowRuleSetSnapshot Summary(string name, string scopeJson) =>
        new(Guid.NewGuid(),
            name,
            Description: null,
            scopeJson,
            Enabled: true,
            Body: $"the text of {name}",
            ContentSha256: $"hash-of-{name}",
            Version: 1,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1);
}
