namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.ClientModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Contracts.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Drafting;
using XE_Local_AI_Engine.Client.Services.Drafting.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Designer;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>The designer's fix-up loop over a scripted model, every proposal judged by the REAL definition service (parser, tool gate, lint).</summary>
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowDesignerServiceTests
{
    private const string LocalModel = "qwen3-8b:Q4_K_M";

    private const string WorkingGraph = """
                                        { "schemaVersion": 1,
                                          "nodes": [{ "key": "start", "kind": "Start", "position": { "x": 0, "y": 0 } },
                                                    { "key": "summarize", "kind": "LlmCall", "config": { "prompt": "Summarize.", "inputBindings": { "doc": "run.input.document" } } },
                                                    { "key": "end", "kind": "End", "config": { "outcome": "completed", "resultPath": "input.output.text" } }],
                                          "edges": [{ "key": "e1", "from": "start", "to": "summarize" }, { "key": "e2", "from": "summarize", "to": "end" }] }
                                        """;

    /// <summary>
    ///     The working graph with one real change (the prompt) and every other member written out at its parser default,
    ///     plus moved positions: exactly one change is real.
    /// </summary>
    private const string ValidProposal = """
                                         { "schemaVersion": 1, "kind": "Standard", "chat": null,
                                           "nodes": [{ "key": "start", "kind": "Start", "label": "start", "joinPolicy": "All", "maxAttempts": 1, "timeoutSeconds": null,
                                                       "position": { "x": 40, "y": 40 }, "config": {} },
                                                     { "key": "summarize", "kind": "LlmCall", "label": "summarize", "joinPolicy": null, "maxAttempts": 3, "timeoutSeconds": null,
                                                       "position": { "x": 280, "y": 0 }, "config": { "prompt": "Summarize in three bullets.", "model": null, "inputBindings": { "doc": "run.input.document" } } },
                                                     { "key": "end", "kind": "End", "label": null, "joinPolicy": null, "maxAttempts": null, "timeoutSeconds": null,
                                                       "position": null, "config": { "outcome": "completed", "resultPath": "input.output.text", "publishToChat": false } }],
                                           "edges": [{ "key": "e1", "from": "start", "to": "summarize", "label": null, "condition": null },
                                                     { "key": "e2", "from": "summarize", "to": "end", "label": null, "condition": null }] }
                                         """;

    /// <summary>A relational operator against a boolean, which the parser refuses on the edge.</summary>
    private const string InvalidProposal = """
                                           { "schemaVersion": 1,
                                             "nodes": [{ "key": "start", "kind": "Start", "config": {} },
                                                       { "key": "summarize", "kind": "LlmCall", "config": { "prompt": "Summarize." } },
                                                       { "key": "end", "kind": "End", "config": { "outcome": "completed" } }],
                                             "edges": [{ "key": "e1", "from": "start", "to": "summarize" },
                                                       { "key": "e2", "from": "summarize", "to": "end", "condition": { "path": "output.text", "op": "Gt", "value": true } }] }
                                           """;

    [Test]
    public async Task Turn_WhenTheFirstProposalIsInvalid_FeedsTheErrorsBackAndReturnsTheValidRetry()
    {
        var harness = new Harness(Envelope(InvalidProposal), Envelope(ValidProposal));

        var turn = await harness.TurnAsync(GraphWorkflowDesignerIntent.Change);

        AssertEx.Equal(expected: 2, turn.Attempts);
        AssertEx.True(AssertEx.NotNull(turn.Validation).IsValid, "the retry's proposal is the one returned");
        AssertEx.Contains(harness.Local.LastUserMessage(callIndex: 1), "[e2]", message: "the parser's error goes back keyed by its element");
        AssertEx.Contains(harness.Local.LastUserMessage(callIndex: 1), GraphWorkflowDesignerService.WholeChangeReply, message: "the retry's reply must describe the whole change, not the fix");
        AssertEx.True(harness.Local.IsDisposed, "the per-turn local client is disposed");
        AssertEx.Equal(expected: 220L, turn.InputTokens, "tokens are summed over every attempt");
    }

    [Test]
    public async Task Turn_WhenTheProposalTripsTheLint_FeedsTheFindingBackAsMustFix()
    {
        var harness = new Harness(Envelope(ExclusiveBranches(endJoinPolicy: "All")), Envelope(ExclusiveBranches(endJoinPolicy: "Any")));

        var turn = await harness.TurnAsync(GraphWorkflowDesignerIntent.Change);

        AssertEx.Equal(expected: 2, turn.Attempts, "a lint finding is retried although the graph validates");
        AssertEx.Contains(harness.Local.LastUserMessage(callIndex: 1), "mutually exclusive branches of 'classify'");
        AssertEx.Empty(AssertEx.NotNull(turn.Validation).Warnings);
    }

    [Test]
    public async Task Turn_WhenAChangeTurnComesBackWithoutAProposal_NudgesOnceAndReturnsTheProposal()
    {
        var harness = new Harness("""{ "reply": "Sure.", "proposal": null }""", Envelope(ValidProposal));

        var turn = await harness.TurnAsync(GraphWorkflowDesignerIntent.Change);

        AssertEx.Equal(expected: 2, turn.Attempts);
        AssertEx.NotNull(turn.ProposalGraphJson);
        AssertEx.Contains(harness.Local.LastUserMessage(callIndex: 1), "You were asked to change the workflow");
        AssertEx.Contains(harness.Local.LastUserMessage(callIndex: 1), GraphWorkflowDesignerService.WholeChangeReply);
    }

    [Test]
    public async Task Turn_WhenStillInvalidAfterTheRetries_ReturnsTheLastProposalMarkedInvalid()
    {
        var harness = new Harness(Envelope(InvalidProposal), Envelope(InvalidProposal), Envelope(InvalidProposal), Envelope(ValidProposal));

        var turn = await harness.TurnAsync(GraphWorkflowDesignerIntent.Change);

        AssertEx.Equal(expected: 1 + GraphWorkflowDesignerService.MaxFixUpRetries, turn.Attempts, "two retries, then the proposal is returned as it stands");
        AssertEx.NotNull(turn.ProposalGraphJson);
        var validation = AssertEx.NotNull(turn.Validation);
        AssertEx.False(validation.IsValid);
        AssertEx.Contains(validation.Errors, static error => error.Key == "e2");
    }

    [Test]
    public async Task Turn_WithAskIntent_RepliesOnlyEvenWhenTheModelProposes()
    {
        var harness = new Harness(Envelope(ValidProposal, reply: "It summarizes the document."));

        var turn = await harness.TurnAsync(GraphWorkflowDesignerIntent.Ask);

        AssertEx.Equal("It summarizes the document.", turn.Reply);
        AssertEx.Null(turn.ProposalGraphJson);
        AssertEx.Null(turn.Validation);
        AssertEx.Empty(turn.Changes);
        AssertEx.Equal(expected: 1, turn.Attempts);
        AssertEx.Contains(harness.Local.SystemPrompt(callIndex: 0), "This turn is a question");
    }

    /// <summary>The diff runs on the parsed graphs: defaults written out and moved positions are no change, a new prompt is.</summary>
    [Test]
    public async Task Turn_ChangeList_IgnoresDefaultsAndPositions()
    {
        var harness = new Harness(Envelope(ValidProposal));

        var turn = await harness.TurnAsync(GraphWorkflowDesignerIntent.Change);

        AssertEx.Equal(expected: 1, turn.Changes.Count, $"one real change: {string.Join(", ", turn.Changes)}");
        AssertEx.Equal(new GraphWorkflowDesignerChange
            {
                Kind = "node",
                Key = "summarize",
                Change = "modified"
            },
            turn.Changes[0]);
    }

    [Test]
    public void ChangeList_OverAnEmptyCanvas_AddsEveryElement()
    {
        var changes = GraphWorkflowDesignerChanges.Compute(string.Empty, ValidProposal, maxNodes: 200);

        AssertEx.Equal("node:end:added, node:start:added, node:summarize:added, edge:e1:added, edge:e2:added",
            string.Join(", ", changes.Select(static change => $"{change.Kind}:{change.Key}:{change.Change}")));
    }

    /// <summary>
    ///     A step inserted after Start, the model shifting every edge key down by one: only the rerouted edge is removed and
    ///     the two new connections are added, under fresh keys; the edges it kept keep their working keys.
    /// </summary>
    [Test]
    public async Task Turn_WhenTheModelReusesEdgeKeysForNewConnections_ReportsOnlyTheRealEdgeChanges()
    {
        const string working = """
                               { "nodes": [{ "key": "start", "kind": "Start" }, {{Steps}}, { "key": "end", "kind": "End", "config": { "outcome": "completed" } }],
                                 "edges": [{ "key": "e1", "from": "start", "to": "draft" }, { "key": "e2", "from": "draft", "to": "review" },
                                           { "key": "e3", "from": "review", "to": "end" }] }
                               """;
        const string proposal = """
                                { "nodes": [{ "key": "start", "kind": "Start" }, {{Steps}}, {{Outline}}, { "key": "end", "kind": "End", "config": { "outcome": "completed" } }],
                                  "edges": [{ "key": "e1", "from": "start", "to": "outline" }, { "key": "e2", "from": "outline", "to": "draft" },
                                            { "key": "e3", "from": "draft", "to": "review" }, { "key": "e4", "from": "review", "to": "end" }] }
                                """;
        var harness = new Harness(Envelope(WithSteps(proposal)));

        var turn = await harness.TurnAsync(GraphWorkflowDesignerIntent.Change, WithSteps(working));

        AssertEx.Equal("node:outline:added, edge:e1:removed, edge:e5:added, edge:e6:added",
            string.Join(", ", turn.Changes.Select(static change => $"{change.Kind}:{change.Key}:{change.Change}")));
        using var proposed = JsonDocument.Parse(AssertEx.NotNull(turn.ProposalGraphJson));
        AssertEx.Equal("e5:start>outline, e6:outline>draft, e2:draft>review, e3:review>end",
            string.Join(", ", proposed.RootElement.GetProperty("edges").EnumerateArray()
                                      .Select(static edge => $"{edge.GetProperty("key").GetString()}:{edge.GetProperty("from").GetString()}>{edge.GetProperty("to").GetString()}")),
            "the proposal handed back carries the aligned keys the change list names");

        static string WithSteps(string graph) =>
            graph.Replace("{{Steps}}", """
                                       { "key": "draft", "kind": "LlmCall", "config": { "prompt": "Draft.", "inputBindings": { "doc": "run.input.document" } } },
                                       { "key": "review", "kind": "LlmCall", "config": { "prompt": "Review.", "inputBindings": { "doc": "run.input.document" } } }
                                       """, StringComparison.Ordinal)
                 .Replace("{{Outline}}", """{ "key": "outline", "kind": "LlmCall", "config": { "prompt": "Outline.", "inputBindings": { "doc": "run.input.document" } } }""",
                      StringComparison.Ordinal);
    }

    [Test]
    public async Task Turn_WhenTheModelRenumbersTheEdgesOfAnUnchangedGraph_ReportsNoChange()
    {
        var harness = new Harness(Envelope(ValidProposal.Replace("\"e1\"", "\"edge_b\"", StringComparison.Ordinal).Replace("\"e2\"", "\"e1\"", StringComparison.Ordinal)
                                                        .Replace("\"edge_b\"", "\"e2\"", StringComparison.Ordinal)));

        var turn = await harness.TurnAsync(GraphWorkflowDesignerIntent.Change);

        AssertEx.Equal("node:summarize:modified", string.Join(", ", turn.Changes.Select(static change => $"{change.Kind}:{change.Key}:{change.Change}")),
            "the swapped edge keys are no change; the prompt is the only one");
    }

    /// <summary>Parallel edges between the same two nodes pair with the working ones one to one, in order; one past them keeps its own new key.</summary>
    [Test]
    public void NormalizeEdgeKeys_PairsParallelEdgesOneToOne()
    {
        const string working = """
                               { "edges": [{ "key": "e1", "from": "start", "to": "approve" }, { "key": "e2", "from": "approve", "to": "next" },
                                           { "key": "e3", "from": "approve", "to": "next" }, { "key": "e4", "from": "next", "to": "end" }] }
                               """;
        var proposal = new GraphWorkflowDesignerGraph
        {
            Edges =
            [
                Edge("e1", "start", "approve"),
                Edge("e3", "approve", "next"),
                Edge("e3", "approve", "next"),
                Edge("e9", "approve", "next"),
                Edge("e4", "next", "end")
            ]
        };

        var normalized = GraphWorkflowDesignerChanges.NormalizeEdgeKeys(working, proposal);

        AssertEx.Equal("e1, e3, e2, e9, e4", string.Join(", ", AssertEx.NotNull(normalized.Edges).Select(static edge => edge.Key)));

        static GraphWorkflowDesignerEdge Edge(string key, string from, string to) =>
            new()
            {
                Key = key,
                From = from,
                To = to
            };
    }

    [Test]
    public async Task Turn_OnALocalThinkingModel_TurnsThinkingOffAndPinsTheModel()
    {
        var harness = new Harness(Envelope(ValidProposal));

        _ = await harness.TurnAsync(GraphWorkflowDesignerIntent.Change);

        var options = AssertEx.NotNull(harness.Local.Options[0]);
        AssertEx.Equal(true, options.AdditionalProperties?["xe.llama.disable_thinking"], "the llama.cpp thinking-off marker rides the call");
        AssertEx.Equal(LocalModel, options.ModelId);
        _ = harness.Provider.Received(1).CreateChatClient(Arg.Is<LocalModelSelection>(static selection => selection.ResidencyIntent == ModelResidencyIntent.Transient));
    }

    [Test]
    public async Task Turn_OnACloudModelWithoutTheLocalDataGrant_RefusesNamingTheGrantAndCallsNoModel()
    {
        var harness = new Harness(Envelope(ValidProposal))
        {
            Locality = ModelTrustLocality.Cloud
        };

        var result = await harness.Service.TurnAsync(Harness.Request(GraphWorkflowDesignerIntent.Change, model: "ext:conn/gpt"));

        AssertEx.Equal<GraphWorkflowDesignerFailureKind?>(GraphWorkflowDesignerFailureKind.CloudLocalDataWithheld, result.Failure);
        AssertEx.Contains(result.FailureMessage, "Grant local data");
        AssertEx.Equal(expected: 0, harness.Local.Calls + harness.Cloud.Calls, "a refused turn reaches no model");
        AssertEx.True(harness.Gate.TryAcquire(out var lease), "the refusal happens before the slot is taken");
        lease?.Dispose();
    }

    [Test]
    public async Task Turn_OnACloudModelWithTheLocalDataGrant_RunsOnTheCloudRoutingClient()
    {
        var harness = new Harness(Envelope(ValidProposal))
        {
            Locality = ModelTrustLocality.Cloud,
            Grants = new ExternalProviderCloudGrants { LocalData = true }
        };

        var result = await harness.Service.TurnAsync(Harness.Request(GraphWorkflowDesignerIntent.Change, model: "ext:conn/gpt"));

        AssertEx.NotNull(result.Turn);
        AssertEx.Equal(expected: 1, harness.Cloud.Calls);
        AssertEx.Equal(expected: 0, harness.Local.Calls);
        AssertEx.Equal("ext:conn/gpt", harness.Cloud.Options[0]?.ModelId, "the routing client picks the provider off the model id");
    }

    [Test]
    public async Task Turn_WhileARunHoldsTheNode_IsRefusedAsBusy()
    {
        var harness = new Harness(Envelope(ValidProposal));
        harness.Dispatcher.SlotHolder.Returns(new InvocationState
        {
            Status = InvocationStatus.Running
        });

        var result = await harness.Service.TurnAsync(Harness.Request(GraphWorkflowDesignerIntent.Change));

        AssertEx.Equal<GraphWorkflowDesignerFailureKind?>(GraphWorkflowDesignerFailureKind.NodeBusy, result.Failure);
        AssertEx.Equal(expected: 0, harness.Local.Calls);
    }

    [Test]
    public async Task Turn_WhenTheModelNeverAnswersTheEnvelope_FailsUnparseableWithoutItsText()
    {
        const string RawText = "I cannot do that.";
        var harness = new Harness(RawText, RawText, RawText);

        var result = await harness.Service.TurnAsync(Harness.Request(GraphWorkflowDesignerIntent.Auto));

        AssertEx.Equal<GraphWorkflowDesignerFailureKind?>(GraphWorkflowDesignerFailureKind.Unparseable, result.Failure);
        AssertEx.False(result.FailureMessage?.Contains(RawText, StringComparison.Ordinal) ?? false, "model text never reaches a failure");
        AssertEx.Equal(expected: 3, harness.Local.Calls);
    }

    /// <summary>
    ///     The schema actually sent: llama.cpp refuses a boolean subschema with an HTTP 500, so none may appear outside
    ///     <c>additionalProperties</c>, and every member stays required in declaration order.
    /// </summary>
    [Test]
    public async Task Turn_SendsASchemaWithNoBooleanSubschemaAndEveryMemberRequired()
    {
        var harness = new Harness(Envelope(ValidProposal));

        _ = await harness.TurnAsync(GraphWorkflowDesignerIntent.Change);

        var format = AssertEx.NotNull(harness.Local.Options[0]?.ResponseFormat as ChatResponseFormatJson, "every attempt carries the JSON schema response format");
        AssertEx.True(format.Schema.HasValue, "the format carries a schema");
        var schema = format.Schema!.Value;
        var booleans = new List<string>();
        CollectBooleanSubschemas(schema, "#", booleans);
        AssertEx.Empty(booleans, $"boolean subschemas: {string.Join(", ", booleans)} in {schema.GetRawText()}");

        var proposal = schema.GetProperty("properties").GetProperty("proposal").GetProperty("properties");
        var node = proposal.GetProperty("nodes").GetProperty("items");
        AssertEx.Equal("""{"type":["object","null"]}""", node.GetProperty("properties").GetProperty("config").GetRawText());
        AssertEx.Equal("key, kind, label, joinPolicy, maxAttempts, timeoutSeconds, position, config",
            string.Join(", ", node.GetProperty("required").EnumerateArray().Select(static member => member.GetString())),
            "every node member is required, in the graph document's order");

        AssertEx.Equal("""{"type":"string"}""", node.GetProperty("properties").GetProperty("key").GetRawText(), "a node key is a non-null string");
        var kind = node.GetProperty("properties").GetProperty("kind");
        AssertEx.Equal("string", kind.GetProperty("type").GetString(), "a node kind is a non-null string");
        AssertEx.Equal(Enum.GetNames<GraphWorkflowNodeKind>().Length, kind.GetProperty("enum").GetArrayLength(), "the kind keeps its enum, without null");
        var edge = proposal.GetProperty("edges").GetProperty("items").GetProperty("properties");
        AssertEx.Empty(new[] { "key", "from", "to" }.Where(member => edge.GetProperty(member).GetRawText() != """{"type":"string"}"""),
            $"an edge's key and ends are non-null strings: {edge.GetRawText()}");
        AssertEx.Equal("""{"type":["string","null"]}""", node.GetProperty("properties").GetProperty("label").GetRawText(), "every other member stays nullable");

        var condition = proposal.GetProperty("edges").GetProperty("items").GetProperty("properties").GetProperty("condition");
        AssertEx.Contains(condition.GetRawText(), "\"value\":{\"type\":[\"string\",\"number\",\"boolean\",\"null\"]}");
        AssertEx.Equal("path, op, value",
            string.Join(", ", condition.GetProperty("required").EnumerateArray().Select(static member => member.GetString())),
            "a condition's path is required first, so the grammar cannot drop it");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Turn_WhenTheModelProviderFailsTheRequest_ReturnsProviderFailedWithTheStatusOnly(bool clientResult)
    {
        var harness = new Harness();
        harness.Local.Failure = clientResult
            ? new ClientResultException("JSON schema error at #/properties/secret-text")
            : new HttpRequestException("JSON schema error at #/properties/secret-text", inner: null, HttpStatusCode.InternalServerError);

        var result = await harness.Service.TurnAsync(Harness.Request(GraphWorkflowDesignerIntent.Change));

        AssertEx.Equal<GraphWorkflowDesignerFailureKind?>(GraphWorkflowDesignerFailureKind.ProviderFailed, result.Failure);
        AssertEx.False(result.FailureMessage?.Contains("secret-text", StringComparison.Ordinal) ?? true, "the provider's body never reaches the failure");
        AssertEx.True(harness.Gate.TryAcquire(out var lease), "the slot is released on a provider failure");
        lease?.Dispose();
        AssertEx.True(harness.Local.IsDisposed, "the per-turn client is disposed on a provider failure");
    }

    /// <summary>Every boolean standing where a schema stands; <c>additionalProperties: false</c> is the one llama.cpp accepts.</summary>
    private static void CollectBooleanSubschemas(JsonElement schema, string path, List<string> found)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            found.Add(path);
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var member in schema.EnumerateObject())
        {
            switch (member.Name)
            {
                case "properties" or "$defs" when member.Value.ValueKind == JsonValueKind.Object:
                    foreach (var property in member.Value.EnumerateObject())
                    {
                        CollectBooleanSubschemas(property.Value, $"{path}/{member.Name}/{property.Name}", found);
                    }

                    break;
                case "items" or "not":
                    CollectBooleanSubschemas(member.Value, $"{path}/{member.Name}", found);
                    break;
                case "anyOf" or "oneOf" or "allOf" when member.Value.ValueKind == JsonValueKind.Array:
                    var index = 0;
                    foreach (var branch in member.Value.EnumerateArray())
                    {
                        CollectBooleanSubschemas(branch, $"{path}/{member.Name}/{index++}", found);
                    }

                    break;
                case "additionalProperties" when member.Value.ValueKind != JsonValueKind.False:
                    CollectBooleanSubschemas(member.Value, $"{path}/{member.Name}", found);
                    break;
            }
        }
    }

    /// <summary>Lint left after the retries makes the designer's report invalid, so the proposal cannot be accepted.</summary>
    [Test]
    public async Task Turn_WhenTheLintSurvivesEveryRetry_ReportsItAsErrorsAndInvalid()
    {
        var lints = Envelope(ExclusiveBranches(endJoinPolicy: "All"));
        var harness = new Harness(lints, lints, lints);

        var turn = await harness.TurnAsync(GraphWorkflowDesignerIntent.Change);

        AssertEx.Equal(expected: 3, turn.Attempts);
        var validation = AssertEx.NotNull(turn.Validation);
        AssertEx.False(validation.IsValid, "a proposal the lint still flags must not be acceptable");
        AssertEx.Equal(expected: 1, validation.Errors.Count(static error => error.Key == "end"), "the finding is an error exactly once");
        AssertEx.Empty(validation.Warnings.Where(static warning => warning.Key == "end"), "and no longer a warning beside it");
    }

    [Test]
    public async Task Turn_WhenAReplyOnlyAnswerHasABlankReply_RetriesAndFailsUnparseableAtTheEnd()
    {
        const string Blank = """{ "reply": "  ", "proposal": null }""";
        var harness = new Harness(Blank, Blank, Blank);

        var result = await harness.Service.TurnAsync(Harness.Request(GraphWorkflowDesignerIntent.Auto));

        AssertEx.Equal<GraphWorkflowDesignerFailureKind?>(GraphWorkflowDesignerFailureKind.Unparseable, result.Failure);
        AssertEx.Equal(expected: 3, harness.Local.Calls, "a blank reply spends the same retry budget as no envelope");
    }

    [Test]
    public async Task Turn_WhenABlankReplyIsRetried_ReturnsTheRetrysReply()
    {
        var harness = new Harness("""{ "reply": null, "proposal": null }""", """{ "reply": "It summarizes.", "proposal": null }""");

        var turn = await harness.TurnAsync(GraphWorkflowDesignerIntent.Ask);

        AssertEx.Equal("It summarizes.", turn.Reply);
        AssertEx.Equal(expected: 2, turn.Attempts);
    }

    [Test]
    public async Task Turn_WhenAProposalComesWithABlankReply_SaysWhatChangedInFixedWords()
    {
        var harness = new Harness(Envelope(ValidProposal, reply: " "));

        var turn = await harness.TurnAsync(GraphWorkflowDesignerIntent.Change);

        AssertEx.Equal("Proposed changes: 0 added, 1 modified, 0 removed.", turn.Reply);
    }

    [Test]
    public void ChangeList_WhenTheGraphKindChanges_ReportsTheSettings()
    {
        var changes = GraphWorkflowDesignerChanges.Compute(WorkingGraph, WorkingGraph.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"kind\": \"Chat\",", StringComparison.Ordinal), maxNodes: 200);

        AssertEx.Contains(changes, static change => change is { Kind: "graph", Key: "settings", Change: "modified" });
    }

    [Test]
    public void ChangeList_WhenTheChatSettingsChange_ReportsTheSettingsOnly()
    {
        var changes = GraphWorkflowDesignerChanges.Compute(ChatGraph(chat: null), ChatGraph("""{ "acceptsAttachments": true }"""), maxNodes: 200);

        AssertEx.Equal("graph:settings:modified", string.Join(", ", changes.Select(static change => $"{change.Kind}:{change.Key}:{change.Change}")));
    }

    [Test]
    public void ChangeList_WhenTheSettingsDifferOnlyByDefaults_ReportsNothing()
    {
        AssertEx.Empty(GraphWorkflowDesignerChanges.Compute(ChatGraph(chat: null), ChatGraph("""{ "acceptsAttachments": false, "requireRerunConfirmation": true }"""), maxNodes: 200));
        AssertEx.Empty(GraphWorkflowDesignerChanges.Compute(WorkingGraph, WorkingGraph.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"kind\": \"Standard\", \"chat\": null,", StringComparison.Ordinal), maxNodes: 200));
    }

    /// <summary>The raw fallback (an empty canvas does not parse) compares the settings with the same defaults.</summary>
    [Test]
    public void ChangeList_OverAnEmptyCanvas_ReportsAChatProposalsSettings()
    {
        var changes = GraphWorkflowDesignerChanges.Compute(string.Empty, ChatGraph(chat: null), maxNodes: 200);

        AssertEx.Contains(changes, static change => change is { Kind: "graph", Key: "settings", Change: "modified" });
    }

    private static string ChatGraph(string? chat) =>
        $$"""
          { "schemaVersion": 1, "kind": "Chat"{{(chat is null ? string.Empty : $", \"chat\": {chat}")}},
            "nodes": [{ "key": "start", "kind": "Start" }, { "key": "end", "kind": "End", "config": { "outcome": "completed" } }],
            "edges": [{ "key": "e1", "from": "start", "to": "end" }] }
          """;

    /// <summary>An empty canvas still carries its kind: rebuilding a Chat workflow as Standard is a settings change, and the prompt says Chat.</summary>
    [Test]
    public async Task Turn_OnAnEmptyChatCanvas_ReportsTheSettingsChangeAndShowsTheKind()
    {
        var harness = new Harness(Envelope(ValidProposal));
        var request = Harness.Request(GraphWorkflowDesignerIntent.Change);

        var result = await harness.Service.TurnAsync(new GraphWorkflowDesignerTurnRequest
        {
            ModelName = request.ModelName,
            Messages = request.Messages,
            GraphJson = """{"schemaVersion":1,"nodes":[],"edges":[],"kind":"Chat"}""",
            Intent = request.Intent
        });

        var turn = AssertEx.NotNull(result.Turn, $"the turn failed: {result.FailureMessage}");
        AssertEx.Contains(turn.Changes, static change => change is { Kind: "graph", Key: "settings", Change: "modified" });
        AssertEx.Contains(harness.Local.SystemPrompt(callIndex: 0), "\"kind\":\"Chat\"", message: "the prompt shows the empty canvas's kind");
    }

    [Test]
    public async Task Turn_WhenTheReplyIsLongerThanAMessage_CapsItWithAnEllipsis()
    {
        var harness = new Harness(JsonSerializer.Serialize(new { reply = new string('r', 9000), proposal = (object?)null }));

        var turn = await harness.TurnAsync(GraphWorkflowDesignerIntent.Ask);

        AssertEx.True(turn.Reply.Length <= GraphWorkflowDesignerTurnRequest.MaxMessageLength, $"the reply is {turn.Reply.Length} characters");
        AssertEx.True(turn.Reply.EndsWith('…'), "the cut is marked");
    }

    [Test]
    public async Task Turn_WhenTheGraphPlusTheConversationExceedsTheBudget_RefusesBeforeTheSlot()
    {
        var harness = new Harness(Envelope(ValidProposal));
        var request = Harness.Request(GraphWorkflowDesignerIntent.Change);
        harness.Dispatcher.SlotHolder.Returns(_ => throw new InvalidOperationException("the slot must not be asked for"));

        var result = await harness.Service.TurnAsync(new GraphWorkflowDesignerTurnRequest
        {
            ModelName = request.ModelName,
            Messages = request.Messages,
            GraphJson = WorkingGraph + new string(' ', new DraftingOptions().MaxPromptChars),
            Intent = request.Intent
        });

        AssertEx.Equal<GraphWorkflowDesignerFailureKind?>(GraphWorkflowDesignerFailureKind.InvalidRequest, result.Failure);
        AssertEx.Contains(result.FailureMessage, "The workflow plus the conversation is too large for the designer");
        AssertEx.Equal(expected: 0, harness.Local.Calls);
    }

    [Test]
    [Arguments("""{ "reply": "Here.", "proposal": { "schemaVersion": 1, "nodes": [null], "edges": [] } }""")]
    [Arguments("""{ "reply": "Here.", "proposal": { "schemaVersion": 1, "nodes": [], "edges": [null] } }""")]
    public async Task Turn_WhenEveryProposalHoldsANullElement_RetriesAndFailsUnparseable(string answer)
    {
        var harness = new Harness(answer, answer, answer);

        var result = await harness.Service.TurnAsync(Harness.Request(GraphWorkflowDesignerIntent.Change));

        AssertEx.Equal<GraphWorkflowDesignerFailureKind?>(GraphWorkflowDesignerFailureKind.Unparseable, result.Failure);
        AssertEx.Equal(expected: 3, harness.Local.Calls, "a null element spends the same retry budget as no envelope");
    }

    [Test]
    public async Task Turn_WhenAProposalHasNullArrays_ReadsThemAsEmptyAndReportsItInvalid()
    {
        const string NullArrays = """{ "reply": "Here.", "proposal": { "schemaVersion": 1, "nodes": null, "edges": null } }""";
        var harness = new Harness(NullArrays, NullArrays, NullArrays);

        var turn = await harness.TurnAsync(GraphWorkflowDesignerIntent.Change);

        AssertEx.False(AssertEx.NotNull(turn.Validation).IsValid);
        AssertEx.Contains(AssertEx.NotNull(turn.ProposalGraphJson), "\"nodes\":[]");
    }

    /// <summary>A history trimmed from the front can open with the assistant; the model must still see the user speak first.</summary>
    [Test]
    public async Task Turn_WhenTheHistoryOpensWithTheAssistant_DropsTheLeadingAssistantMessages()
    {
        var harness = new Harness(Envelope(ValidProposal));
        var request = Harness.Request(GraphWorkflowDesignerIntent.Change);

        var result = await harness.Service.TurnAsync(new GraphWorkflowDesignerTurnRequest
        {
            ModelName = request.ModelName,
            Messages =
            [
                Message(GraphWorkflowDesignerRole.Assistant, "Earlier answer."),
                Message(GraphWorkflowDesignerRole.User, "Add a step."),
                Message(GraphWorkflowDesignerRole.Assistant, "Added."),
                Message(GraphWorkflowDesignerRole.User, "Now a review.")
            ],
            GraphJson = WorkingGraph,
            Intent = request.Intent
        });

        AssertEx.NotNull(result.Turn, $"the turn failed: {result.FailureMessage}");
        AssertEx.Equal("system, user, assistant, user", string.Join(", ", harness.Local.Roles(callIndex: 0)));
        AssertEx.Equal("Add a step.", harness.Local.MessageText(callIndex: 0, index: 1));
    }

    private static GraphWorkflowDesignerMessage Message(GraphWorkflowDesignerRole role, string text) =>
        new()
        {
            Role = role,
            Text = text
        };

    /// <summary>
    ///     The designer on the Azure OpenAI v1 path, through the real MEAI OpenAI adapter and a capturing transport: the wire
    ///     carries the designer's own schema as a json_schema response format, and nothing turns strict mode on.
    /// </summary>
    [Test]
    public async Task Turn_OnTheAzureOpenAiPath_SendsTheDesignersSchemaAsAJsonSchemaResponseFormat()
    {
        using var capture = new CapturingHandler(Envelope(ValidProposal));
        using var httpClient = new HttpClient(capture);
        var openAiClient = new AzureFoundryChatClientFactory().CreateOpenAiV1ClientForTesting(new StoredAzureFoundryConnection
            {
                Endpoint = "https://example.openai.azure.com/",
                AuthMode = AzureFoundryAuthMode.ApiKey,
                ApiSurface = AzureFoundryApiSurface.OpenAiV1,
                ApiKey = "test-key",
                Headers = [],
                Models = [new StoredAzureFoundryModel { DeploymentName = "gpt-4o" }]
            },
            httpClient);
        using var cloudClient = openAiClient.GetChatClient("gpt-4o").AsIChatClient();
        var harness = new Harness
        {
            Locality = ModelTrustLocality.Cloud,
            Grants = new ExternalProviderCloudGrants { LocalData = true },
            CloudClient = cloudClient
        };

        var result = await harness.Service.TurnAsync(Harness.Request(GraphWorkflowDesignerIntent.Change, model: "gpt-4o"));

        AssertEx.NotNull(result.Turn, $"the turn failed: {result.Failure} {result.FailureMessage}");
        using var body = JsonDocument.Parse(AssertEx.NotNull(capture.LastBody, "the request reached the transport"));
        var format = body.RootElement.GetProperty("response_format");
        AssertEx.Equal("json_schema", format.GetProperty("type").GetString());
        var jsonSchema = format.GetProperty("json_schema");
        AssertEx.False(jsonSchema.TryGetProperty("strict", out var strict) && strict.ValueKind == JsonValueKind.True, $"strict must be absent or false: {jsonSchema.GetRawText()}");
        AssertEx.Equal("graph_workflow_designer_turn", jsonSchema.GetProperty("name").GetString());
        AssertEx.True(JsonElement.DeepEquals(GraphWorkflowDesignerService.ResponseFormat.Schema!.Value, jsonSchema.GetProperty("schema")),
            $"the wire schema is the designer's, unchanged: {jsonSchema.GetProperty("schema").GetRawText()}");
    }

    private static string Envelope(string proposal, string reply = "Done.") =>
        $$"""{ "reply": {{JsonSerializer.Serialize(reply)}}, "proposal": {{proposal}} }""";

    private static string ExclusiveBranches(string endJoinPolicy) =>
        $$"""
          { "nodes": [{ "key": "start", "kind": "Start" },
                      { "key": "classify", "kind": "DecisionModel", "config": { "question": "Which?", "labels": ["bug", "feature"], "inputBindings": { "m": "run.input.message" } } },
                      { "key": "bug", "kind": "LlmCall", "config": { "prompt": "Bug.", "inputBindings": { "m": "run.input.message" } } },
                      { "key": "feature", "kind": "LlmCall", "config": { "prompt": "Feature.", "inputBindings": { "m": "run.input.message" } } },
                      { "key": "end", "kind": "End", "joinPolicy": "{{endJoinPolicy}}", "config": { "outcome": "completed" } }],
            "edges": [{ "key": "e1", "from": "start", "to": "classify" },
                      { "key": "e2", "from": "classify", "to": "bug", "condition": { "path": "output.choice", "op": "Eq", "value": "bug" } },
                      { "key": "e3", "from": "classify", "to": "feature", "condition": { "path": "output.choice", "op": "Eq", "value": "feature" } },
                      { "key": "e4", "from": "bug", "to": "end" },
                      { "key": "e5", "from": "feature", "to": "end" }] }
          """;

    /// <summary>A transport that keeps the request body and answers with one chat completion carrying <c>content</c>.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _content;

        public CapturingHandler(string content)
        {
            _content = content;
        }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var completion = JsonSerializer.Serialize(new
            {
                id = "chatcmpl-test",
                @object = "chat.completion",
                created = 0,
                model = "gpt-4o",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = _content }, finish_reason = "stop" } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(completion, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class Harness
    {
        private GraphWorkflowDesignerService? _service;

        public Harness(params string[] script)
        {
            Local = new ScriptedChatClient(script);
            Cloud = new ScriptedChatClient(script);
        }

        public ScriptedChatClient Local { get; }

        public ScriptedChatClient Cloud { get; }

        /// <summary>The cloud routing client the service gets; the scripted <see cref="Cloud" /> unless a test drives a real adapter.</summary>
        public IChatClient? CloudClient { get; init; }

        public ILocalModelProvider Provider { get; } = Substitute.For<ILocalModelProvider>();

        public IWorkerEventDispatcher Dispatcher { get; } = Substitute.For<IWorkerEventDispatcher>();

        public DraftAdmissionGate Gate { get; private set; } = null!;

        public ModelTrustLocality Locality { get; init; } = ModelTrustLocality.Local;

        public ExternalProviderCloudGrants Grants { get; init; } = ExternalProviderCloudGrants.None;

        public GraphWorkflowDesignerService Service => _service ??= Build();

        public static GraphWorkflowDesignerTurnRequest Request(GraphWorkflowDesignerIntent intent, string model = LocalModel, string graphJson = WorkingGraph) =>
            new()
            {
                ModelName = model,
                Messages =
                [
                    new GraphWorkflowDesignerMessage
                    {
                        Role = GraphWorkflowDesignerRole.User,
                        Text = "Make the summary three bullets."
                    }
                ],
                GraphJson = graphJson,
                Intent = intent
            };

        public async Task<GraphWorkflowDesignerTurn> TurnAsync(GraphWorkflowDesignerIntent intent, string graphJson = WorkingGraph)
        {
            var result = await Service.TurnAsync(Request(intent, graphJson: graphJson));
            return AssertEx.NotNull(result.Turn, $"the turn failed: {result.Failure} {result.FailureMessage}");
        }

        private GraphWorkflowDesignerService Build()
        {
            var classifications = Substitute.For<IModelClassificationStore>();
            classifications.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                           .Returns(Task.FromResult<ModelClassificationRecord?>(new ModelClassificationRecord
                           {
                               ModelName = LocalModel,
                               Digest = null,
                               DetectedKind = ModelKind.Chat,
                               DetectedCapabilitiesJson = null,
                               OverrideKind = null,
                               DetectedAtUtc = null,
                               UpdatedAtUtc = 0
                           }));
            var ggufModels = Substitute.For<IGgufModelStore>();
            ggufModels.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([
                          new LocalModelDescriptor
                          {
                              ModelName = LocalModel,
                              ProviderName = "llamacpp",
                              IsAvailable = true,
                              SizeBytes = 1024,
                              ModifiedAt = DateTimeOffset.UnixEpoch,
                              MaxContextTokens = 8192
                          }
                      ]));

            Provider.ProviderName.Returns("llamacpp");
            Provider.CreateChatClient(Arg.Any<LocalModelSelection>()).Returns(Local);
            var resolver = Substitute.For<ILocalModelProviderResolver>();
            resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Provider));

            var trust = Substitute.For<IModelTrustResolver>();
            trust.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Locality);
            trust.ResolveCloudGrantsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Grants);
            var runtimeSettings = StubNodeRuntimeSettings.Create().WithAllowCloudModelAccess(false).Build();

            // The real definition service, so the parser, tool gate and lint the loop reacts to are the ones a save runs.
            var definitions = new GraphWorkflowDefinitionService(Substitute.For<IGraphWorkflowStore>(),
                Substitute.For<IToolInvocationService>(),
                runtimeSettings,
                resolver,
                ggufModels,
                trust,
                Options.Create(new GraphWorkflowOptions()));

            var capabilities = Substitute.For<IModelCapabilityResolver>();
            capabilities.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                        .Returns(new ModelCapabilitySnapshot(SupportsThinking: true, SupportsTools: false, IsCloud: false));
            var agents = Substitute.For<IAgentDefinitionService>();
            agents.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AgentDefinitionRecord>>([]));
            var nodeSettings = Substitute.For<INodeSettingsStore>();
            nodeSettings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());

            Gate = new DraftAdmissionGate(Dispatcher);
            return new GraphWorkflowDesignerService(definitions,
                agents,
                resolver,
                ggufModels,
                classifications,
                Substitute.For<IOllamaModelService>(),
                capabilities,
                trust,
                runtimeSettings,
                CloudClient ?? Cloud,
                Gate,
                Options.Create(new DraftingOptions()),
                Options.Create(new GraphWorkflowOptions()),
                nodeSettings,
                TimeProvider.System,
                NullLogger<GraphWorkflowDesignerService>.Instance);
        }
    }

    /// <summary>
    ///     A chat client that answers from a script and keeps a snapshot of every call, because the service appends to the
    ///     same message list between attempts. Hand-written: a substitute records the list by reference.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<string> _script;
        private readonly List<IReadOnlyList<ChatMessage>> _calls = [];

        public ScriptedChatClient(IEnumerable<string> script)
        {
            _script = new Queue<string>(script);
        }

        public int Calls => _calls.Count;

        /// <summary>When set, every call throws it, as a provider that refuses the request does.</summary>
        public Exception? Failure { get; set; }

        public List<ChatOptions?> Options { get; } = [];

        public bool IsDisposed { get; private set; }

        public string LastUserMessage(int callIndex) =>
            _calls[callIndex].Last(static message => message.Role == ChatRole.User).Text;

        public string SystemPrompt(int callIndex) =>
            _calls[callIndex][0].Text;

        public IEnumerable<string> Roles(int callIndex) =>
            _calls[callIndex].Select(static message => message.Role.Value);

        public string MessageText(int callIndex, int index) =>
            _calls[callIndex][index].Text;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls.Add([.. messages]);
            Options.Add(options);
            if (Failure is not null)
            {
                throw Failure;
            }

            var text = _script.Count > 0 ? _script.Dequeue() : throw new InvalidOperationException("The script ran out: the service asked more often than the test expects.");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 110,
                    OutputTokenCount = 20
                }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            // The designer is non-streaming; an empty stream suffices.
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
