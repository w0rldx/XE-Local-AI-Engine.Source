namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What a node inherits when the step before it is a routing decision rather than a producer. The seeded
///     <c>feature-development-v1</c> shape puts a human gate before its decomposition and a join before its
///     verification, so this is the difference between those two nodes being handed the plan and being handed nothing.
/// </summary>
public sealed class DevWorkflowUpstreamArtifactTests
{
    private const string SeededAgentId = "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b";

    /// <summary>A producer, a gate, and the node that has to work on what the producer made.</summary>
    private const string AgentGateAgent = $$"""
                                            {
                                              "schemaVersion": 1,
                                              "nodes": [
                                                { "nodeKey": "plan", "nodeType": "Agent", "label": "Plan", "agentDefinitionId": "{{SeededAgentId}}" },
                                                { "nodeKey": "planapproval", "nodeType": "HumanGate", "label": "Approve the plan" },
                                                { "nodeKey": "decompose", "nodeType": "Agent", "label": "Decompose", "agentDefinitionId": "{{SeededAgentId}}" }
                                              ],
                                              "edges": [
                                                { "from": "plan", "to": "planapproval" },
                                                { "from": "planapproval", "to": "decompose", "condition": { "path": "decision", "op": "eq", "value": "Approve" } }
                                              ]
                                            }
                                            """;

    /// <summary>Two producing branches meeting at a join, and the node after it that must see both.</summary>
    private const string TwoBranchesThroughAJoin = $$"""
                                                     {
                                                       "schemaVersion": 1,
                                                       "nodes": [
                                                         { "nodeKey": "fan", "nodeType": "Parallel" },
                                                         { "nodeKey": "left", "nodeType": "Agent", "label": "Left", "agentDefinitionId": "{{SeededAgentId}}" },
                                                         { "nodeKey": "right", "nodeType": "Agent", "label": "Right", "agentDefinitionId": "{{SeededAgentId}}" },
                                                         { "nodeKey": "join", "nodeType": "Join" },
                                                         { "nodeKey": "verify", "nodeType": "Agent", "label": "Verify", "agentDefinitionId": "{{SeededAgentId}}" }
                                                       ],
                                                       "edges": [
                                                         { "from": "fan", "to": "left" },
                                                         { "from": "fan", "to": "right" },
                                                         { "from": "left", "to": "join" },
                                                         { "from": "right", "to": "join" },
                                                         { "from": "join", "to": "verify" }
                                                       ]
                                                     }
                                                     """;

    /// <summary><see cref="AgentGateAgent" /> with a producer of its own in front of the plan node.</summary>
    private const string TwoProducersThenGate = $$"""
                                                  {
                                                    "schemaVersion": 1,
                                                    "nodes": [
                                                      { "nodeKey": "research", "nodeType": "Agent", "label": "Research", "agentDefinitionId": "{{SeededAgentId}}" },
                                                      { "nodeKey": "plan", "nodeType": "Agent", "label": "Plan", "agentDefinitionId": "{{SeededAgentId}}" },
                                                      { "nodeKey": "planapproval", "nodeType": "HumanGate", "label": "Approve the plan" },
                                                      { "nodeKey": "decompose", "nodeType": "Agent", "label": "Decompose", "agentDefinitionId": "{{SeededAgentId}}" }
                                                    ],
                                                    "edges": [
                                                      { "from": "research", "to": "plan" },
                                                      { "from": "plan", "to": "planapproval" },
                                                      { "from": "planapproval", "to": "decompose", "condition": { "path": "decision", "op": "eq", "value": "Approve" } }
                                                    ]
                                                  }
                                                  """;

    [ClassDataSource<DevWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required DevWorkflowHostFixture Host { get; init; }

    /// <summary>
    ///     The finding the live run produced: a gate makes nothing, so a node behind one inheriting only its immediate
    ///     predecessors inherits nothing — and the decompose agent was asked to split a plan it had never been shown.
    /// </summary>
    [Test]
    public async Task ANodeBehindAHumanGate_InheritsTheArtifactsOfTheProducerBehindIt()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(AgentGateAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "plan", "plan.md", "1. Add Subtract").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "plan").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.DecideAsync(runId, "planapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var promoted = AssertEx.NotNull((await harness.ReadArtifactsAsync(runId).ConfigureAwait(false)).SingleOrDefault());
        var consumed = await harness.ReadConsumedArtifactIdsAsync(runId, "decompose").ConfigureAwait(false);
        AssertEx.Equal(expected: 1, consumed.Count, "the gate is a routing decision, not a dead end for the plan it approved.");
        AssertEx.Contains(consumed, promoted.Id);
    }

    /// <summary>
    ///     A join is the other structural dead end, and the one that has to widen rather than pass through: the node
    ///     after it inherits from EVERY branch that reached it, not from whichever one the walk happens to find first.
    /// </summary>
    [Test]
    public async Task ANodeBehindAJoin_InheritsEveryBranchsProducers()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(TwoBranchesThroughAJoin).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "left", "left.md", "the left branch").ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "right", "right.md", "the right branch").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "left").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "right").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var artifacts = await harness.ReadArtifactsAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, artifacts.Count);
        var consumed = await harness.ReadConsumedArtifactIdsAsync(runId, "verify").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, consumed.Count, "both branches are upstream of the verification, and a join hides neither.");
        foreach (var artifact in artifacts)
        {
            AssertEx.Contains(consumed, artifact.Id);
        }
    }

    /// <summary>
    ///     The walk stops at the first producer on each path. Research is upstream of the decomposition too, but the
    ///     plan is what the plan node made OF it — handing over both would hand the consumer the superseded input
    ///     beside the output and let it work from either.
    /// </summary>
    [Test]
    public async Task AProducerInBetween_ShadowsTheOneFurtherBack()
    {
        await using var harness = new DevWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(TwoProducersThenGate).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "research", "research.md", "what the code does today").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        _ = await harness.SaveAgentArtifactAsync(runId, "plan", "plan.md", "1. Add Subtract").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "plan").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.DecideAsync(runId, "planapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var artifacts = await harness.ReadArtifactsAsync(runId).ConfigureAwait(false);
        var plan = AssertEx.NotNull(artifacts.SingleOrDefault(artifact => artifact.Name == "plan.md"));
        var consumed = await harness.ReadConsumedArtifactIdsAsync(runId, "decompose").ConfigureAwait(false);
        AssertEx.Equal(expected: 1, consumed.Count, "the plan node is a producer, so the walk back ends there.");
        AssertEx.Contains(consumed, plan.Id);
    }
}
