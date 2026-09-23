namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Decisions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The chat-workflow additions to the graph contract: every refusal the parser adds, and a Standard graph reading as before.</summary>
/// <remarks>Covers the top-level <c>kind</c>/<c>chat</c>, the node chat flags, and the <c>ChatInput</c> and <c>DecisionModel</c> kinds.</remarks>
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowChatContractTests
{
    [Test]
    public void AStandardGraph_ReadsAsStandardWithNoChatSettingsAndPublishesNothing()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd);

        AssertEx.Equal(GraphWorkflowDefinitionKind.Standard, graph.Kind, "a graph that names no kind is Standard.");
        AssertEx.Null(graph.Chat, "a Standard graph carries no chat settings.");
        AssertEx.False(AssertEx.NotNull(graph.Nodes["done"].Config as GraphWorkflowEndConfig).PublishToChat, "an End publishes by default only in a Chat graph.");
        AssertEx.False(AssertEx.NotNull(graph.Nodes["analyze"].Config as GraphWorkflowAgentConfig).PublishToChat);
    }

    [Test]
    public void AChatGraph_ReadsItsKindSettingsAndTheEndPublishDefault()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ChatInputAnswer);

        AssertEx.Equal(GraphWorkflowDefinitionKind.Chat, graph.Kind);
        AssertEx.Equal(new GraphWorkflowChatSettings { AcceptsAttachments = false, RequireRerunConfirmation = true }, graph.Chat);
        AssertEx.True(AssertEx.NotNull(graph.Nodes["done"].Config as GraphWorkflowEndConfig).PublishToChat, "an End of a Chat graph publishes unless told not to.");
        AssertEx.Equal("Which database?", AssertEx.NotNull(graph.Nodes["ask"].Config as GraphWorkflowChatInputConfig).Prompt);
        AssertEx.Equal(expected: 1, graph.Nodes["ask"].MaxAttempts, "a wait gets one try, like a pause.");
    }

    [Test]
    public void AChatGraphWithoutAChatBlock_ReadsTheDefaultsAndAnExplicitFalseEndStaysQuiet()
    {
        var graph = GraphWorkflowGraph.Parse(Chat(ending: """{ "outcome": "completed", "publishToChat": false }"""));

        AssertEx.Equal(GraphWorkflowChatSettings.Default, graph.Chat);
        AssertEx.False(AssertEx.NotNull(graph.Nodes["done"].Config as GraphWorkflowEndConfig).PublishToChat);
    }

    [Test]
    [Arguments(""" "kind": "Workflow", """, "must be one of Standard, Chat")]
    [Arguments(""" "kind": 1, """, "must be one of Standard, Chat")]
    [Arguments(""" "chat": { "acceptsAttachments": true }, """, "apply to a Chat graph only")]
    [Arguments(""" "kind": "Chat", "chat": { "acceptsFiles": true }, """, "declare 'acceptsFiles', which nothing reads")]
    [Arguments(""" "kind": "Chat", "chat": { "acceptsAttachments": "yes" }, """, "The 'chat.acceptsAttachments' setting must be true or false")]
    [Arguments(""" "kind": "Chat", "chat": [], """, "'chat' settings must be an object")]
    public void TheGraphLevelChatContract_RefusesWhatNothingReads(string topLevel, string expected)
    {
        var json = $$"""
                     { "schemaVersion": 1, {{topLevel}}
                       "nodes": [{ "key": "start", "kind": "Start" }, { "key": "done", "kind": "End", "config": { "outcome": "completed" } }],
                       "edges": [{ "key": "e1", "from": "start", "to": "done" }] }
                     """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(json)).Message, expected);
    }

    [Test]
    public void PublishToChat_IsRefusedOutsideAChatGraph()
    {
        var json = GraphWorkflowGraphs.StartAgentEnd.Replace("\"instructions\": \"Analyze the input.\"", "\"instructions\": \"Analyze the input.\", \"publishToChat\": true", StringComparison.Ordinal);

        AssertEx.Contains(Refusal(json), "sets 'publishToChat', which only a Chat graph reads");
    }

    [Test]
    public void PublishToChat_IsReadOnAnAgentOfAChatGraph()
    {
        var graph = GraphWorkflowGraph.Parse(Chat(agent: """{ "instructions": "Go.", "publishToChat": true }"""));

        AssertEx.True(AssertEx.NotNull(graph.Nodes["work"].Config as GraphWorkflowAgentConfig).PublishToChat);
    }

    [Test]
    public void PublishToChat_OnAKindThatHasNoAnswer_IsAStrayMember()
    {
        var json = Chat(agent: """{ "instructions": "Go." }""")
            .Replace("""{ "key": "start", "kind": "Start" }""", """{ "key": "start", "kind": "Start", "config": { "publishToChat": true } }""", StringComparison.Ordinal);

        AssertEx.Contains(Refusal(json), "declares 'publishToChat', which no Start node reads");
    }

    [Test]
    public void IncludeAttachments_IsRefusedUnlessTheGraphAcceptsAttachments()
    {
        AssertEx.Contains(Refusal(Chat(agent: """{ "instructions": "Go.", "includeAttachments": true }""")),
            "sets 'includeAttachments', but the graph does not accept attachments");

        var graph = GraphWorkflowGraph.Parse(Chat(agent: """{ "instructions": "Go.", "includeAttachments": true }""", chat: """{ "acceptsAttachments": true }"""));
        AssertEx.True(AssertEx.NotNull(graph.Nodes["work"].Config as GraphWorkflowAgentConfig).IncludeAttachments);
    }

    [Test]
    public void AChatInput_IsRefusedInAStandardGraph()
    {
        var json = GraphWorkflowGraphs.ChatInputAnswer
                                      .Replace("\"kind\": \"Chat\",", string.Empty, StringComparison.Ordinal)
                                      .Replace("\"chat\": { \"acceptsAttachments\": false, \"requireRerunConfirmation\": true },", string.Empty, StringComparison.Ordinal);

        var refusal = AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(json));

        AssertEx.Contains(refusal.Result.Errors.Single(error => error.Key == "ask").Message, "only a Chat graph can carry");
    }

    [Test]
    public void AChatInputWithOnlyConditionalWaysOut_IsRefused()
    {
        var json = GraphWorkflowGraphs.ChatInputAnswer.Replace("""{ "key": "e2", "from": "ask", "to": "done" }""",
            """{ "key": "e2", "from": "ask", "to": "done", "condition": { "path": "output.text", "op": "ne", "value": "stop" } }""",
            StringComparison.Ordinal);

        AssertEx.Contains(Refusal(json), "no unconditional outbound edge");
    }

    [Test]
    public void AChatInputWithoutAPrompt_IsRefused() =>
        AssertEx.Contains(Refusal(GraphWorkflowGraphs.ChatInputAnswer.Replace("""{ "prompt": "Which database?" }""", "{}", StringComparison.Ordinal)),
            "needs a non-empty 'prompt'");

    [Test]
    public void APauseOfferingAnswer_IsRefused()
    {
        var json = GraphWorkflowGraphs.PauseTwoDecisions.Replace("""["Approve", "Reject"]""", """["Approve", "Answer"]""", StringComparison.Ordinal);

        AssertEx.Contains(Refusal(json), "offers 'Answer', which only a ChatInput node takes");
    }

    [Test]
    public void TheNodeAfterAChatInput_IsWarnedThatItReadsTheAnswerDocument()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ChatInputAnswer);

        var warning = graph.Warnings.First(entry => entry.Key == "done");
        AssertEx.Contains(warning.Message, "reached only through the Pause or ChatInput node(s) 'ask'");
    }

    [Test]
    public void ADecisionModel_ReadsItsConfigAndIsWork()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.DecisionModelRouting("Classify the request"));
        var node = graph.Nodes["classify"];
        var config = AssertEx.NotNull(node.Config as GraphWorkflowDecisionModelConfig);

        AssertEx.Equal("Classify the request", config.Question);
        AssertEx.Equal("coding,research,general", string.Join(",", config.Labels));
        AssertEx.Equal("llm", config.Provider);
        AssertEx.Null(config.Model);
        AssertEx.Equal("run.input.message", config.InputBindings["request"]);
        AssertEx.Equal(GraphWorkflowDefinitionKind.Standard, graph.Kind, "a DecisionModel is not a chat-only kind.");

        var defaulted = GraphWorkflowGraph.Parse(DecisionModel("""{ "question": "Q?", "labels": ["a", "b"] }"""));
        AssertEx.Equal("llm", AssertEx.NotNull(defaulted.Nodes["decide"].Config as GraphWorkflowDecisionModelConfig).Provider, "an omitted provider is the one there is.");
        AssertEx.Equal(expected: 3, defaulted.Nodes["decide"].MaxAttempts, "a decision is model work, so it gets the work default.");
    }

    [Test]
    [Arguments("""{ "question": "Q?", "labels": ["only"] }""", "declares 1 label(s)")]
    [Arguments("""{ "question": "Q?", "labels": ["a", "a"] }""", "declares the label 'a' twice")]
    [Arguments("""{ "question": "Q?", "labels": ["a", " "] }""", "not a non-empty string of at most 64 characters")]
    [Arguments("""{ "question": "Q?", "labels": ["a", 2] }""", "not a non-empty string of at most 64 characters")]
    [Arguments("""{ "question": "Q?" }""", "needs a 'labels' array")]
    [Arguments("""{ "labels": ["a", "b"] }""", "needs a non-empty 'question'")]
    [Arguments("""{ "question": "Q?", "labels": ["a", "b"], "provider": "laya" }""", "names the decision provider 'laya'")]
    [Arguments("""{ "question": "Q?", "labels": ["a", "b"], "prompt": "x" }""", "declares 'prompt', which no DecisionModel node reads")]
    public void ADecisionModel_RefusesAMalformedConfig(string config, string expected) =>
        AssertEx.Contains(Refusal(DecisionModel(config)), expected);

    [Test]
    public void ADecisionModel_RefusesALabelOverSixtyFourCharactersAndMoreThanThirtyTwoLabels()
    {
        AssertEx.Contains(Refusal(DecisionModel($$"""{ "question": "Q?", "labels": ["a", "{{new string('x', 65)}}"] }""")), "at most 64 characters");

        var many = string.Join(", ", Enumerable.Range(0, 33).Select(static index => $"\"l{index}\""));
        AssertEx.Contains(Refusal(DecisionModel($$"""{ "question": "Q?", "labels": [{{many}}] }""")), "declares 33 label(s)");

        var most = string.Join(", ", Enumerable.Range(0, 32).Select(static index => $"\"l{index}\""));
        AssertEx.Equal(expected: 32, AssertEx.NotNull(GraphWorkflowGraph.Parse(DecisionModel($$"""{ "question": "Q?", "labels": [{{most}}] }""")).Nodes["decide"].Config as GraphWorkflowDecisionModelConfig).Labels.Count);
    }

    /// <summary>
    ///     The lowering is the whole of the provider's contract with the lane: an enum grammar over the labels, reasoning
    ///     off, the question as the prompt and the node's bindings unchanged.
    /// </summary>
    [Test]
    public void TheLlmProvider_LowersToAnEnumConstrainedLlmCall()
    {
        var config = AssertEx.NotNull(GraphWorkflowGraph.Parse(GraphWorkflowGraphs.DecisionModelRouting("Classify it")).Nodes["classify"].Config as GraphWorkflowDecisionModelConfig);
        var provider = new LlmDecisionProvider();

        var lowered = provider.Lower(config);

        AssertEx.Equal("llm", provider.Name);
        AssertEx.True(lowered.Prompt.StartsWith("Classify it", StringComparison.Ordinal), "the question leads the prompt.");
        AssertEx.Contains(lowered.Prompt, "coding, research, general");
        AssertEx.Equal("none", lowered.ReasoningEffort);
        AssertEx.Equal(0f, AssertEx.NotNull(lowered.SamplingOptions).Temperature);
        AssertEx.NotNull(lowered.SystemPrompt);
        AssertEx.Equal("run.input.message", lowered.InputBindings["request"]);
        var schema = lowered.ResponseJsonSchema ?? throw new AssertionException("the lowering must carry a response schema.");
        AssertEx.Equal("object", schema.GetProperty("type").GetString());
        AssertEx.Equal("choice", schema.GetProperty("required")[0].GetString());
        AssertEx.Equal("coding,research,general",
            string.Join(",", schema.GetProperty("properties").GetProperty("choice").GetProperty("enum").EnumerateArray().Select(static label => label.GetString())));
    }

    [Test]
    public void TheLlmProvider_ReadsTheChoiceAndReportsNoScores()
    {
        var provider = new LlmDecisionProvider();
        using var answer = JsonDocument.Parse("""{"choice":"coding"}""");

        var read = provider.Interpret("""{"choice":"coding"}""", answer.RootElement);
        var none = provider.Interpret("no json", json: null);

        AssertEx.Equal("coding", read.Choice);
        AssertEx.Null(read.Confidence, "a grammar-constrained answer carries no calibrated confidence.");
        AssertEx.Null(read.Probabilities);
        AssertEx.Null(none.Choice);
    }

    private static string Refusal(string json) =>
        AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(json)).Message;

    /// <summary>A Chat graph <c>Start → work (Agent) → done (End)</c>, with the agent, End and chat block the caller names.</summary>
    private static string Chat(string agent = """{ "instructions": "Go." }""", string ending = """{ "outcome": "completed" }""", string? chat = null) =>
        $$"""
          { "schemaVersion": 1, "kind": "Chat", {{(chat is null ? string.Empty : $"\"chat\": {chat},")}}
            "nodes": [{ "key": "start", "kind": "Start" },
                      { "key": "work", "kind": "Agent", "config": {{agent}} },
                      { "key": "done", "kind": "End", "config": {{ending}} }],
            "edges": [{ "key": "e1", "from": "start", "to": "work" }, { "key": "e2", "from": "work", "to": "done" }] }
          """;

    private static string DecisionModel(string config) =>
        $$"""
          { "schemaVersion": 1,
            "nodes": [{ "key": "start", "kind": "Start" },
                      { "key": "decide", "kind": "DecisionModel", "config": {{config}} },
                      { "key": "done", "kind": "End", "config": { "outcome": "completed" } }],
            "edges": [{ "key": "e1", "from": "start", "to": "decide" }, { "key": "e2", "from": "decide", "to": "done" }] }
          """;
}
