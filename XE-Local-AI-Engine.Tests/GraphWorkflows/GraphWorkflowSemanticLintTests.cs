namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The semantic lint warnings, each with a negative twin, because a false positive nags an operator who drew a correct graph.</summary>
/// <remarks>The shipped samples are held to no warning at all, these included, by <see cref="GraphWorkflowSampleContractTests" />.</remarks>
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowSemanticLintTests
{
    private const string Classify = """
                                    { "key": "classify", "kind": "DecisionModel",
                                      "config": { "question": "Which?", "labels": ["bug", "feature"], "inputBindings": { "m": "run.input.message" } } }
                                    """;

    [Test]
    public void ExclusiveBranches_IntoAnAllJoin_WarnOnTheJoinNamingTheDecision()
    {
        var graph = Parse(ExclusiveBranchesInto(joinPolicy: null));

        var warning = Single(graph.SemanticLintWarnings);
        AssertEx.Equal("end", warning.Key);
        AssertEx.Contains(warning.Message, "mutually exclusive branches of 'classify'");
        AssertEx.Contains(graph.Warnings, candidate => candidate == warning, "the lint rides in the graph's warnings, so the validate route and the save report it.");
    }

    [Test]
    public void ExclusiveBranches_IntoAnAnyJoin_DoNotWarn()
    {
        AssertEx.Empty(Parse(ExclusiveBranchesInto(joinPolicy: "Any")).SemanticLintWarnings);
    }

    [Test]
    public void ParallelExclusiveEdges_FromTheDecisionStraightIntoAnAllJoin_Warn()
    {
        var graph = Parse($$"""
                            { "nodes": [{ "key": "start", "kind": "Start" }, {{Classify}}, {{End("end", joinPolicy: null, resultPath: null)}}],
                              "edges": [{ "key": "e1", "from": "start", "to": "classify" },
                                        { "key": "e2", "from": "classify", "to": "end", "condition": { "path": "output.choice", "op": "Eq", "value": "bug" } },
                                        { "key": "e3", "from": "classify", "to": "end", "condition": { "path": "output.choice", "op": "Eq", "value": "feature" } }] }
                            """);

        AssertEx.Equal("end", Single(graph.SemanticLintWarnings).Key);
    }

    /// <summary>A content edge that bypasses the decision can satisfy the join on its own, so the branch shape is not the trap.</summary>
    [Test]
    public void ABranchJoinedByAnEdgeThatBypassesTheDecision_DoesNotWarn()
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

        AssertEx.Empty(graph.SemanticLintWarnings);
    }

    /// <summary>Comparisons the lint cannot prove disjoint (here a threshold pair) are left alone: the rule is narrow on purpose.</summary>
    [Test]
    public void BranchesItCannotProveExclusive_DoNotWarn()
    {
        AssertEx.Empty(Parse(ExclusiveBranchesInto(joinPolicy: null, firstOp: "Gte", secondOp: "Lt")).SemanticLintWarnings);
    }

    [Test]
    public void UpstreamBinding_ToANodeThatIsNotAnImmediatePredecessor_Warns()
    {
        var graph = Parse($$"""
                            { "nodes": [{ "key": "start", "kind": "Start" }, {{Llm("first", "run.input.message")}},
                                        {{Llm("second", "upstream.start.output.input")}}, {{End("end", joinPolicy: null, resultPath: null)}}],
                              "edges": [{ "key": "e1", "from": "start", "to": "first" },
                                        { "key": "e2", "from": "first", "to": "second" },
                                        { "key": "e3", "from": "second", "to": "end" }] }
                            """);

        var warning = Single(graph.SemanticLintWarnings);
        AssertEx.Equal("second", warning.Key);
        AssertEx.Contains(warning.Message, "'upstream.start.output.input'");
        AssertEx.Contains(warning.Message, "Add an edge from 'start' to 'second'");
    }

    [Test]
    public void UpstreamBinding_ToAnImmediatePredecessor_DoesNotWarn()
    {
        var graph = Parse($$"""
                            { "nodes": [{ "key": "start", "kind": "Start" }, {{Llm("first", "run.input.message")}},
                                        {{Llm("second", "upstream.first.output.text")}}, {{End("end", joinPolicy: null, resultPath: "upstream.second.output.text")}}],
                              "edges": [{ "key": "e1", "from": "start", "to": "first" },
                                        { "key": "e2", "from": "first", "to": "second" },
                                        { "key": "e3", "from": "second", "to": "end" }] }
                            """);

        AssertEx.Empty(graph.SemanticLintWarnings);
    }

    /// <summary>The rule reads every member that resolves against the input document, an End's resultPath included.</summary>
    [Test]
    public void ResultPath_ToANodeThatIsNotAnImmediatePredecessor_Warns()
    {
        var graph = Parse($$"""
                            { "nodes": [{ "key": "start", "kind": "Start" }, {{Llm("first", "run.input.message")}},
                                        {{Llm("second", "input.output.text")}}, {{End("end", joinPolicy: null, resultPath: "upstream.first.output.text")}}],
                              "edges": [{ "key": "e1", "from": "start", "to": "first" },
                                        { "key": "e2", "from": "first", "to": "second" },
                                        { "key": "e3", "from": "second", "to": "end" }] }
                            """);

        var warning = Single(graph.SemanticLintWarnings);
        AssertEx.Equal("end", warning.Key);
        AssertEx.Contains(warning.Message, "resultPath");
    }

    [Test]
    public void InputShortcut_OnAnAllJoinWithTwoPredecessors_Warns()
    {
        var graph = Parse(TwoPredecessorsReading("input.output.text", joinPolicy: null));

        var warning = Single(graph.SemanticLintWarnings);
        AssertEx.Equal("merge", warning.Key);
        AssertEx.Contains(warning.Message, "2 predecessors");
        AssertEx.Contains(warning.Message, "read 'input.<nodeKey>.…' or 'upstream.<nodeKey>.…'");
    }

    /// <summary>With several predecessors 'input' is their outputs keyed by node key, so the whole map and a keyed path read it correctly.</summary>
    [Test]
    [Arguments("input")]
    [Arguments("input.left.output.text")]
    public void InputMapPaths_OnAnAllJoinWithTwoPredecessors_DoNotWarn(string path)
    {
        AssertEx.Empty(Parse(TwoPredecessorsReading(path, joinPolicy: null)).SemanticLintWarnings);
    }

    [Test]
    public void ResultPathOfTheWholeInputMap_OnAnEndWithTwoPredecessors_DoesNotWarn()
    {
        var graph = Parse($$"""
                            { "nodes": [{ "key": "start", "kind": "Start" }, {{Llm("left", "run.input.message")}}, {{Llm("right", "run.input.message")}},
                                        {{End("end", joinPolicy: null, resultPath: "input")}}],
                              "edges": [{ "key": "e1", "from": "start", "to": "left" }, { "key": "e2", "from": "start", "to": "right" },
                                        { "key": "e3", "from": "left", "to": "end" }, { "key": "e4", "from": "right", "to": "end" }] }
                            """);

        AssertEx.Empty(graph.SemanticLintWarnings);
    }

    [Test]
    public void InputShortcut_WithASinglePredecessor_DoesNotWarn()
    {
        var graph = Parse($$"""
                            { "nodes": [{ "key": "start", "kind": "Start" }, {{Llm("first", "run.input.message")}},
                                        {{Llm("second", "input.output.text")}}, {{End("end", joinPolicy: null, resultPath: "input.output.text")}}],
                              "edges": [{ "key": "e1", "from": "start", "to": "first" },
                                        { "key": "e2", "from": "first", "to": "second" },
                                        { "key": "e3", "from": "second", "to": "end" }] }
                            """);

        AssertEx.Empty(graph.SemanticLintWarnings);
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
    public void KeyedInputPath_OnAnAnyJoinAfterAFanOut_DoesNotWarn()
    {
        AssertEx.Empty(Parse(FanOutReading("input.left.output.text")).SemanticLintWarnings);
    }

    /// <summary>Behind the exclusive branches of one Condition only one predecessor ever succeeds, so the shortcut is that one's output.</summary>
    [Test]
    public void InputShortcut_OnAnAnyJoinBehindExclusiveConditionBranches_DoesNotWarn()
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

        AssertEx.Empty(graph.SemanticLintWarnings);
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
    public void UpstreamBinding_OnAnAllJoinWithTwoPredecessors_DoesNotWarn()
    {
        AssertEx.Empty(Parse(TwoPredecessorsReading("upstream.left.output.text", joinPolicy: null)).SemanticLintWarnings);
    }

    /// <summary>A lint finding never makes a graph invalid: the strict parse that refuses on errors accepts every graph above.</summary>
    [Test]
    public void LintFindings_NeverBlockTheParse()
    {
        var graph = GraphWorkflowGraph.Parse(TwoPredecessorsReading("input.output.text", joinPolicy: null));

        AssertEx.NotEmpty(graph.SemanticLintWarnings);
    }

    private static GraphWorkflowValidationError Single(IReadOnlyList<GraphWorkflowValidationError> warnings)
    {
        AssertEx.Equal(expected: 1, warnings.Count, $"expected exactly one lint warning: {string.Join(" | ", warnings)}");
        return warnings[0];
    }

    private static GraphWorkflowGraph Parse(string json)
    {
        var errors = new List<GraphWorkflowValidationError>();
        var graph = GraphWorkflowGraph.Parse(json, int.MaxValue, errors);
        AssertEx.Empty(errors, $"the fixture must be a valid graph: {string.Join(" | ", errors)}");
        return graph;
    }

    private static string ExclusiveBranchesInto(string? joinPolicy, string firstOp = "Eq", string secondOp = "Eq") =>
        $$"""
          { "nodes": [{ "key": "start", "kind": "Start" }, {{Classify}},
                      {{Llm("bug", "run.input.message")}}, {{Llm("feature", "run.input.message")}},
                      {{End("end", joinPolicy, resultPath: null)}}],
            "edges": [{ "key": "e1", "from": "start", "to": "classify" },
                      { "key": "e2", "from": "classify", "to": "bug", "condition": { "path": "output.choice", "op": "{{firstOp}}", "value": "bug" } },
                      { "key": "e3", "from": "classify", "to": "feature", "condition": { "path": "output.choice", "op": "{{secondOp}}", "value": "feature" } },
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
