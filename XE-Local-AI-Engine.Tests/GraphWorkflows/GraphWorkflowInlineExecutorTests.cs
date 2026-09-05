namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The documents the five inline kinds produce, read back off the rows the real tick wrote. Byte-level where the
///     shape is the contract, because every downstream condition is a dot path into these.
/// </summary>
public sealed class GraphWorkflowInlineExecutorTests
{
    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    /// <summary>
    ///     <c>Start</c> hands the run's own input to everything downstream, inside the envelope every kind shares.
    ///     <c>branch</c> is written even when it is null, so a reader can tell "no branch fired" from "this document
    ///     predates branches".
    /// </summary>
    [Test]
    public async Task Start_WrapsTheRunInputInTheCommonEnvelope()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear, """{"seed":1}""").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal("""{"status":"succeeded","attempt":1,"branch":null,"output":{"input":{"seed":1}}}""",
            (await harness.ReadNodeRunAsync(runId, "start").ConfigureAwait(false)).OutputJson);
    }

    /// <summary>
    ///     <c>Parallel</c> and <c>Condition</c> pass their predecessor's <c>output</c> through VERBATIM. Without it a
    ///     Condition's own out-edges — which are evaluated against its own document — would inspect an empty object and
    ///     never fire, which is the defect this rule exists to fix.
    /// </summary>
    [Test]
    public async Task ParallelAndCondition_PassTheirPredecessorsOutputThroughVerbatim()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear, """{"seed":2}""").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal("""{"status":"succeeded","attempt":1,"branch":null,"output":{"input":{"seed":2}}}""",
            (await harness.ReadNodeRunAsync(runId, "middle").ConfigureAwait(false)).OutputJson,
            "the pass-through carries the predecessor's payload, not an empty object.");
    }

    /// <summary>
    ///     The same rule asserted where it is load-bearing: a <c>Condition</c>'s document carries the payload its own
    ///     out-edge then routes on, and the edge that fired is named as the branch.
    /// </summary>
    [Test]
    public async Task AConditionsOwnDocument_CarriesThePayloadItsOutEdgeRoutesOn()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineBranch, """{"requiresReview":true}""").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal("""{"status":"succeeded","attempt":1,"branch":"yes","output":{"input":{"requiresReview":true}}}""",
            (await harness.ReadNodeRunAsync(runId, "check").ConfigureAwait(false)).OutputJson);
    }

    /// <summary>
    ///     <c>Join</c> emits the per-source map over its satisfied inbound edges, so everything downstream of it sees
    ///     every branch rather than whichever one the single-predecessor shortcut would have picked.
    /// </summary>
    [Test]
    public async Task Join_EmitsThePerSourceMapOverItsSatisfiedEdges()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineJoinAll, """{"seed":3}""").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal("""
                       {"status":"succeeded","attempt":1,"branch":null,"output":{"fast":{"status":"succeeded","attempt":1,"branch":null,"output":{"input":{"seed":3}}},"slower":{"status":"succeeded","attempt":1,"branch":null,"output":{"input":{"seed":3}}}}}
                       """,
            (await harness.ReadNodeRunAsync(runId, "merge").ConfigureAwait(false)).OutputJson);
    }

    /// <summary>
    ///     <c>End</c> resolves the author's <c>resultPath</c> against its own input document — which is where the single
    ///     satisfied predecessor's whole document sits.
    /// </summary>
    [Test]
    public async Task End_ResolvesItsResultPathAgainstItsInputDocument()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear, """{"seed":4}""").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal("""{"status":"succeeded","attempt":1,"branch":null,"output":{"outcome":"completed","result":{"input":{"seed":4}}}}""",
            (await harness.ReadNodeRunAsync(runId, "done").ConfigureAwait(false)).OutputJson);
    }

    /// <summary>
    ///     With no <c>resultPath</c> the result is the End node's WHOLE input document. Failing the node over a
    ///     projection nobody named would end a run that did all of its work.
    /// </summary>
    [Test]
    public async Task End_WithNoResultPath_CarriesItsWholeInputDocument()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineJoinAll, """{"seed":5}""").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var document = (await harness.ReadNodeRunAsync(runId, "done").ConfigureAwait(false)).OutputJson;
        AssertEx.Contains(document, "\"result\":{\"run\":{\"input\":{\"seed\":5}}", message: "the whole input document, run input and all.");
        AssertEx.Contains(document, "\"upstream\":{\"merge\"");
    }

    /// <summary>
    ///     The output cap is measured on EVERY hop, which is what makes it worth having: a pass-through chain carries
    ///     the same document forward, and it is the join that doubles it that finally exceeds the cap.
    /// </summary>
    [Test]
    public async Task AnOverCapDocument_FailsTheNodeOutputTooLargeOnTheHopThatExceedsIt()
    {
        // A private host: the cap is host-level configuration, and its floor is 1 KiB.
        await using var harness = new GraphWorkflowHarness(("GraphWorkflows:MaxOutputJsonBytes", "1024"));
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineJoinAll, $$"""{"blob":"{{new string('a', count: 420)}}"}""").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "fast").ConfigureAwait(false)).Status,
            "one document of that size fits, so the hops before the join are unaffected.");

        var merge = await harness.ReadNodeRunAsync(runId, "merge").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, merge.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.OutputTooLarge, merge.FailureClass);
        AssertEx.Equal(GraphWorkflowRunStatus.Failed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }
}
