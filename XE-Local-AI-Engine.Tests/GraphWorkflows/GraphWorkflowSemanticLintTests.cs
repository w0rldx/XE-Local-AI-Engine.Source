namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>The semantic lint findings, each with a negative twin, because a false positive refuses or nags an operator who drew a correct graph.</summary>
/// <remarks>The shipped samples are held to no finding at all, these included, by <see cref="GraphWorkflowSampleContractTests" />.</remarks>
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowSemanticLintTests
{
    private const string Classify = """
                                    { "key": "classify", "kind": "DecisionModel",
                                      "config": { "question": "Which?", "labels": ["bug", "feature"], "inputBindings": { "m": "run.input.message" } } }
                                    """;

    private const string UpstreamSkipsAPredecessor = """
                                                       { "nodes": [{ "key": "start", "kind": "Start" },
                                                                   { "key": "first", "kind": "LlmCall", "config": { "prompt": "Work.", "inputBindings": { "value": "run.input.message" } } },
                                                                   { "key": "second", "kind": "LlmCall", "config": { "prompt": "Work.", "inputBindings": { "value": "upstream.start.output.input" } } },
                                                                   { "key": "end", "kind": "End", "config": { "outcome": "completed" } }],
                                                         "edges": [{ "key": "e1", "from": "start", "to": "first" },
                                                                   { "key": "e2", "from": "first", "to": "second" },
                                                                   { "key": "e3", "from": "second", "to": "end" }] }
                                                       """;

    [Test]
    public void ExclusiveBranches_IntoAnAllJoin_AreAnErrorOnTheJoinNamingTheDecision()
    {
        var graph = Parse(ExclusiveBranchesInto(joinPolicy: null));

        var error = Single(graph.SemanticLintErrors);
        AssertEx.Equal("end", error.Key);
        AssertEx.Contains(error.Message, "mutually exclusive branches of 'classify'");
        AssertEx.Empty(graph.Warnings, "an error is not repeated as a warning");
    }

    [Test]
    public void ExclusiveBranches_IntoAnAnyJoin_AreNoFinding()
    {
        AssertNoFinding(Parse(ExclusiveBranchesInto(joinPolicy: "Any")));
    }

    [Test]
    public void ParallelExclusiveEdges_FromTheDecisionStraightIntoAnAllJoin_AreAnError()
    {
        var graph = Parse($$"""
                            { "nodes": [{ "key": "start", "kind": "Start" }, {{Classify}}, {{End("end", joinPolicy: null, resultPath: null)}}],
                              "edges": [{ "key": "e1", "from": "start", "to": "classify" },
                                        { "key": "e2", "from": "classify", "to": "end", "condition": { "path": "output.choice", "op": "Eq", "value": "bug" } },
                                        { "key": "e3", "from": "classify", "to": "end", "condition": { "path": "output.choice", "op": "Eq", "value": "feature" } }] }
                            """);

        AssertEx.Equal("end", Single(graph.SemanticLintErrors).Key);
    }

    /// <summary>A content edge that bypasses the decision can satisfy the join on its own, so the branch shape is not the trap.</summary>
    [Test]
    public void ABranchJoinedByAnEdgeThatBypassesTheDecision_IsNoFinding()
    {
        var graph = Parse($$"""
                            { "nodes": [{ "key": "start", "kind": "Start" }, {{Llm("draft", "run.input.message")}}, {{Classify}},
                                        {{Llm("polish", "upstream.draft.output.text")}}, {{End("end", joinPolicy: null, resultPath: "upstream.draft.output.text")}},
                                        {{End("other", joinPolicy: null, resultPath: null)}}],
                              "edges": [{ "key": "e1", "from": "start", "to": "draft" },
                                        { "key": "e2", "from": "draft", "to": "classify" },
                                        { "key": "e3", "from": "draft", "to": "polish" },
                                        { "key": "e4", "from": "classify", "to": "end", "condition": { "path": "output.choice", "op": "Eq", "value": "bug" } },
                                        { "key": "e5", "from": "draft", "to": "end" },
                                        { "key": "e6", "from": "classify", "to": "other", "condition": { "path": "output.choice", "op": "Eq", "value": "feature" } },
                                        { "key": "e7", "from": "polish", "to": "other" }] }
                            """);

        AssertNoFinding(graph);
    }

    /// <summary>Comparisons the lint cannot prove disjoint (here a threshold pair) are left alone: the rule is narrow on purpose.</summary>
    [Test]
    public void BranchesItCannotProveExclusive_AreNoFinding()
    {
        AssertNoFinding(Parse(ExclusiveBranchesInto(joinPolicy: null, firstOp: "Gte", secondOp: "Lt")));
    }

    /// <summary>The runtime compares these through decimal or double, where each pair is one value, so both branches can fire and the join runs.</summary>
    [Test]
    [Arguments("0.10000000000000000000000000000001", "0.10000000000000000000000000000002")]
    [Arguments("1.5e30", "1.5000000000000000001e30")]
    public void EqBranchesOnNumbersTheRuntimeRounds_AreNoFinding(string first, string second)
    {
        AssertNoFinding(Parse(ExclusiveBranchesInto(joinPolicy: null, firstValue: first, secondValue: second)));
    }

    [Test]
    public void EqBranchesOnDistinctLongIntegers_AreAnError()
    {
        AssertEx.Equal("end", Single(Parse(ExclusiveBranchesInto(joinPolicy: null, firstValue: "1", secondValue: "2")).SemanticLintErrors).Key);
    }

    [Test]
    public void UpstreamBinding_ToANodeThatIsNotAnImmediatePredecessor_IsAnError()
    {
        var graph = Parse(UpstreamSkipsAPredecessor);

        var error = Single(graph.SemanticLintErrors);
        AssertEx.Equal("second", error.Key);
        AssertEx.Contains(error.Message, "'upstream.start.output.input'");
        AssertEx.Contains(error.Message, "Add an edge from 'start' to 'second'");
    }

    [Test]
    public void UpstreamBinding_ToAnImmediatePredecessor_IsNoFinding()
    {
        var graph = Parse($$"""
                            { "nodes": [{ "key": "start", "kind": "Start" }, {{Llm("first", "run.input.message")}},
                                        {{Llm("second", "upstream.first.output.text")}}, {{End("end", joinPolicy: null, resultPath: "upstream.second.output.text")}}],
                              "edges": [{ "key": "e1", "from": "start", "to": "first" },
                                        { "key": "e2", "from": "first", "to": "second" },
                                        { "key": "e3", "from": "second", "to": "end" }] }
                            """);

        AssertNoFinding(graph);
    }

    /// <summary>The rule reads every member that resolves against the input document, an End's resultPath included.</summary>
    [Test]
    public void ResultPath_ToANodeThatIsNotAnImmediatePredecessor_IsAnError()
    {
        var graph = Parse($$"""
                            { "nodes": [{ "key": "start", "kind": "Start" }, {{Llm("first", "run.input.message")}},
                                        {{Llm("second", "input.output.text")}}, {{End("end", joinPolicy: null, resultPath: "upstream.first.output.text")}}],
                              "edges": [{ "key": "e1", "from": "start", "to": "first" },
                                        { "key": "e2", "from": "first", "to": "second" },
                                        { "key": "e3", "from": "second", "to": "end" }] }
                            """);

        var error = Single(graph.SemanticLintErrors);
        AssertEx.Equal("end", error.Key);
        AssertEx.Contains(error.Message, "resultPath");
    }

    [Test]
    public void InputShortcut_OnAnAllJoinWithTwoPredecessors_Warns()
    {
        var graph = Parse(TwoPredecessorsReading("input.output.text", joinPolicy: null));

        var warning = Single(graph.SemanticLintWarnings);
        AssertEx.Equal("merge", warning.Key);
        AssertEx.Contains(warning.Message, "2 predecessors");
        AssertEx.Contains(warning.Message, "read 'input.<nodeKey>.…' or 'upstream.<nodeKey>.…'");
        AssertEx.Empty(graph.SemanticLintErrors, "the input shortcut stays a warning");
        AssertEx.Contains(graph.Warnings, candidate => candidate == warning, "the lint warning rides in the graph's warnings, so the validate route reports it.");
    }

    /// <summary>With several predecessors 'input' is their outputs keyed by node key, so the whole map and a keyed path read it correctly.</summary>
    [Test]
    [Arguments("input")]
    [Arguments("input.left.output.text")]
    public void InputMapPaths_OnAnAllJoinWithTwoPredecessors_AreNoFinding(string path)
    {
        AssertNoFinding(Parse(TwoPredecessorsReading(path, joinPolicy: null)));
    }

    [Test]
    public void ResultPathOfTheWholeInputMap_OnAnEndWithTwoPredecessors_IsNoFinding()
    {
        var graph = Parse($$"""
                            { "nodes": [{ "key": "start", "kind": "Start" }, {{Llm("left", "run.input.message")}}, {{Llm("right", "run.input.message")}},
                                        {{End("end", joinPolicy: null, resultPath: "input")}}],
                              "edges": [{ "key": "e1", "from": "start", "to": "left" }, { "key": "e2", "from": "start", "to": "right" },
                                        { "key": "e3", "from": "left", "to": "end" }, { "key": "e4", "from": "right", "to": "end" }] }
                            """);

        AssertNoFinding(graph);
    }

    [Test]
    public void InputShortcut_WithASinglePredecessor_IsNoFinding()
    {
        var graph = Parse($$"""
                            { "nodes": [{ "key": "start", "kind": "Start" }, {{Llm("first", "run.input.message")}},
                                        {{Llm("second", "input.output.text")}}, {{End("end", joinPolicy: null, resultPath: "input.output.text")}}],
                              "edges": [{ "key": "e1", "from": "start", "to": "first" },
                                        { "key": "e2", "from": "first", "to": "second" },
                                        { "key": "e3", "from": "second", "to": "end" }] }
                            """);

        AssertNoFinding(graph);
    }

    /// <summary>An Any join after a fan-out admits once every branch settled, so several predecessors succeed and 'input' is their map.</summary>
    [Test]
    public void InputShortcut_OnAnAnyJoinAfterAFanOut_Warns()
    {
        var warning = Single(Parse(FanOutReading("input.output.text")).SemanticLintWarnings);

        AssertEx.Equal("merge", warning.Key);
        AssertEx.Contains(warning.Message, "more than one of them can succeed");
    }

    [Test]
    public void KeyedInputPath_OnAnAnyJoinAfterAFanOut_IsNoFinding()
    {
        AssertNoFinding(Parse(FanOutReading("input.left.output.text")));
    }

    /// <summary>Behind the exclusive branches of one Condition only one predecessor ever succeeds, so the shortcut is that one's output.</summary>
    [Test]
    public void InputShortcut_OnAnAnyJoinBehindExclusiveConditionBranches_IsNoFinding()
    {
        var graph = Parse($$"""
                            { "nodes": [{ "key": "start", "kind": "Start" }, { "key": "route", "kind": "Condition", "config": { "path": "output.input.kind" } },
                                        {{Llm("left", "run.input.message")}}, {{Llm("right", "run.input.message")}},
                                        { "key": "merge", "kind": "LlmCall", "joinPolicy": "Any", "config": { "prompt": "Merge.", "inputBindings": { "text": "input.output.text" } } },
                                        {{End("end", joinPolicy: null, resultPath: null)}}],
                              "edges": [{ "key": "e1", "from": "start", "to": "route" },
                                        { "key": "e2", "from": "route", "to": "left", "condition": { "op": "Eq", "value": "a" } },
                                        { "key": "e3", "from": "route", "to": "right", "condition": { "op": "Eq", "value": "b" } },
                                        { "key": "e4", "from": "left", "to": "merge" }, { "key": "e5", "from": "right", "to": "merge" },
                                        { "key": "e6", "from": "merge", "to": "end" }] }
                            """);

        AssertNoFinding(graph);
    }

    private static string FanOutReading(string path) =>
        $$"""
          { "nodes": [{ "key": "start", "kind": "Start" }, { "key": "fanout", "kind": "Parallel" },
                      {{Llm("left", "run.input.message")}}, {{Llm("right", "run.input.message")}},
                      { "key": "merge", "kind": "LlmCall", "joinPolicy": "Any", "config": { "prompt": "Merge.", "inputBindings": { "text": "{{path}}" } } },
                      {{End("end", joinPolicy: null, resultPath: null)}}],
            "edges": [{ "key": "e1", "from": "start", "to": "fanout" },
                      { "key": "e2", "from": "fanout", "to": "left" }, { "key": "e3", "from": "fanout", "to": "right" },
                      { "key": "e4", "from": "left", "to": "merge" }, { "key": "e5", "from": "right", "to": "merge" },
                      { "key": "e6", "from": "merge", "to": "end" }] }
          """;

    [Test]
    public void UpstreamBinding_OnAnAllJoinWithTwoPredecessors_IsNoFinding()
    {
        AssertNoFinding(Parse(TwoPredecessorsReading("upstream.left.output.text", joinPolicy: null)));
    }

    /// <summary>The lint errors are not part of the parse, so a graph stored before they existed still routes at run start and on every tick.</summary>
    [Test]
    public void LintErrors_DoNotBlockTheStrictParse()
    {
        var graph = GraphWorkflowGraph.Parse(ExclusiveBranchesInto(joinPolicy: null));

        AssertEx.NotEmpty(graph.SemanticLintErrors);
    }

    [Test]
    public async Task ExclusiveBranchesIntoAnAllJoin_RefuseValidateAndSave()
    {
        await AssertRefusedAsync(ExclusiveBranchesInto(joinPolicy: null), "end", "mutually exclusive branches of 'classify'");
    }

    [Test]
    public async Task UpstreamBindingWithoutAnEdge_RefusesValidateAndSave()
    {
        await AssertRefusedAsync(UpstreamSkipsAPredecessor, "second", "has no edge into 'second'");
    }

    [Test]
    public async Task InputShortcutWarning_StillValidatesAsValid()
    {
        var result = await Service().ValidateAsync(TwoPredecessorsReading("input.output.text", joinPolicy: null));

        AssertEx.True(result.IsValid, $"the input shortcut must not refuse: {string.Join(" | ", result.Errors)}");
        AssertEx.Contains(result.Warnings, static warning => warning.Key == "merge", "and is reported as a warning");
    }

    private static async Task AssertRefusedAsync(string graphJson, string key, string message)
    {
        var service = Service();

        var validation = await service.ValidateAsync(graphJson);
        AssertEx.False(validation.IsValid, "validate must refuse the graph");
        AssertEx.Contains(validation.Errors, error => error.Key == key && error.Message.Contains(message, StringComparison.Ordinal), $"validate names the finding: {string.Join(" | ", validation.Errors)}");
        AssertEx.Empty(validation.Warnings.Where(warning => warning.Key == key), "the finding is not repeated as a warning");

        var refused = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() => service.CreateAsync("lint", description: null, graphJson));
        AssertEx.Contains(refused.Result.Errors, error => error.Key == key, "save refuses with the same error");
    }

    /// <summary>The real definition service; no node pins a model or names a tool, so neither resolver nor store is reached.</summary>
    private static GraphWorkflowDefinitionService Service() =>
        new(Substitute.For<IGraphWorkflowStore>(),
            Substitute.For<IToolInvocationService>(),
            StubNodeRuntimeSettings.Create().Build(),
            Substitute.For<ILocalModelProviderResolver>(),
            Substitute.For<IGgufModelStore>(),
            Substitute.For<IModelTrustResolver>(),
            Options.Create(new GraphWorkflowOptions()));

    private static void AssertNoFinding(GraphWorkflowGraph graph)
    {
        AssertEx.Empty(graph.SemanticLintErrors, $"unexpected lint error: {string.Join(" | ", graph.SemanticLintErrors)}");
        AssertEx.Empty(graph.SemanticLintWarnings, $"unexpected lint warning: {string.Join(" | ", graph.SemanticLintWarnings)}");
    }

    private static GraphWorkflowValidationError Single(IReadOnlyList<GraphWorkflowValidationError> findings)
    {
        AssertEx.Equal(expected: 1, findings.Count, $"expected exactly one lint finding: {string.Join(" | ", findings)}");
        return findings[0];
    }

    private static GraphWorkflowGraph Parse(string json)
    {
        var errors = new List<GraphWorkflowValidationError>();
        var graph = GraphWorkflowGraph.Parse(json, int.MaxValue, errors);
        AssertEx.Empty(errors, $"the fixture must be a valid graph: {string.Join(" | ", errors)}");
        return graph;
    }

    private static string ExclusiveBranchesInto(string? joinPolicy,
        string firstOp = "Eq",
        string secondOp = "Eq",
        string firstValue = "\"bug\"",
        string secondValue = "\"feature\"") =>
        $$"""
          { "nodes": [{ "key": "start", "kind": "Start" }, {{Classify}},
                      {{Llm("bug", "run.input.message")}}, {{Llm("feature", "run.input.message")}},
                      {{End("end", joinPolicy, resultPath: null)}}],
            "edges": [{ "key": "e1", "from": "start", "to": "classify" },
                      { "key": "e2", "from": "classify", "to": "bug", "condition": { "path": "output.choice", "op": "{{firstOp}}", "value": {{firstValue}} } },
                      { "key": "e3", "from": "classify", "to": "feature", "condition": { "path": "output.choice", "op": "{{secondOp}}", "value": {{secondValue}} } },
                      { "key": "e4", "from": "bug", "to": "end" },
                      { "key": "e5", "from": "feature", "to": "end" }] }
          """;

    private static string TwoPredecessorsReading(string path, string? joinPolicy) =>
        $$"""
          { "nodes": [{ "key": "start", "kind": "Start" }, {{Llm("left", "run.input.message")}}, {{Llm("right", "run.input.message")}},
                      { "key": "merge", "kind": "LlmCall"{{JoinPolicy(joinPolicy)}}, "config": { "prompt": "Merge.", "inputBindings": { "text": "{{path}}" } } },
                      {{End("end", joinPolicy: null, resultPath: null)}}],
            "edges": [{ "key": "e1", "from": "start", "to": "left" },
                      { "key": "e2", "from": "start", "to": "right" },
                      { "key": "e3", "from": "left", "to": "merge" },
                      { "key": "e4", "from": "right", "to": "merge" },
                      { "key": "e5", "from": "merge", "to": "end" }] }
          """;

    private static string Llm(string key, string binding) =>
        $$"""{ "key": "{{key}}", "kind": "LlmCall", "config": { "prompt": "Work.", "inputBindings": { "value": "{{binding}}" } } }""";

    private static string End(string key, string? joinPolicy, string? resultPath) =>
        $$"""{ "key": "{{key}}", "kind": "End"{{JoinPolicy(joinPolicy)}}, "config": { "outcome": "completed"{{(resultPath is null ? string.Empty : $", \"resultPath\": \"{resultPath}\"")}} } }""";

    private static string JoinPolicy(string? joinPolicy) =>
        joinPolicy is null ? string.Empty : $", \"joinPolicy\": \"{joinPolicy}\"";
}
