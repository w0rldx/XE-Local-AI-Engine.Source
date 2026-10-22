namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     A Tool node that reads node-local data taints its DIRECT consumers: a cloud Agent, LLM call or decision node fed by
///     it runs only while the operator lets cloud models read local data or the model's connection grants it.
/// </summary>
/// <remarks>
///     Every test takes a private agent host with the scripted tool service swapped in and unattended cloud runs ON, so
///     the unattended gate never answers first and the local-data gate is the one under test. A refusal is proven to
///     precede capacity and the package, which precedes any transport.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowLocalDataEdgeTests
{
    private const string KnowledgeTool = "search_knowledge_base";

    private const string ClockTool = "get_current_time";

    [Test]
    public async Task ACloudAgent_FedByAKnowledgeBaseTool_IsRefusedBeforeCapacityAndThePackage()
    {
        const string instructions = "local-data-edge-refused";
        const string model = "local-data-edge-cloud-refused";
        await using var harness = Host();

        var consume = await RunToTheConsumerAsync(harness, Graph(KnowledgeTool, Agent(instructions, model)));

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, consume.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, consume.FailureClass);
        var error = AssertEx.NotNull(consume.Error);
        AssertEx.Contains(error, "Tool node 'kb'", message: "the refusal names the Tool node the content came from.");
        AssertEx.Contains(error, $"'{KnowledgeTool}'", message: "the refusal names the tool.");
        AssertEx.Contains(error, $"the cloud model '{model}'");
        AssertEx.Contains(error, "Node Settings → Privacy & updates");
        AssertEx.Contains(error, "grant local data on the model's external connection");
        AssertEx.False(Sent(harness, instructions), "a refused node builds no package, so nothing is sent.");
        AssertEx.Empty(Capacity(harness).ReservationsFor(model), "a refused node reserves no capacity.");
    }

    [Test]
    public async Task ACloudAgent_WhoseConnectionGrantsLocalData_RunsWithTheSwitchOff()
    {
        const string instructions = "local-data-edge-granted";
        var model = GraphWorkflowModels.Grant("local-data-edge-cloud-granted", new ExternalProviderCloudGrants
        {
            LocalData = true
        });
        await using var harness = Host();

        var consume = await RunToTheConsumerAsync(harness, Graph(KnowledgeTool, Agent(instructions, model)));

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, consume.Status, consume.Error);
        AssertEx.Contains(Prompt(harness, instructions), "the fake tool answered", message: "the granted model reads the Tool node's output.");
    }

    [Test]
    public async Task ACloudAgent_RunsWhileCloudModelsMayReadLocalData()
    {
        const string instructions = "local-data-edge-switch";
        await using var harness = Host(allowCloudModelAccess: true);

        var consume = await RunToTheConsumerAsync(harness, Graph(KnowledgeTool, Agent(instructions, "local-data-edge-cloud-switch")));

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, consume.Status, consume.Error);
        AssertEx.Contains(Prompt(harness, instructions), "the fake tool answered");
    }

    [Test]
    public async Task ACloudAgent_FedByAToolThatReadsNoLocalData_Runs()
    {
        const string instructions = "local-data-edge-clock";
        await using var harness = Host();

        var consume = await RunToTheConsumerAsync(harness, Graph(ClockTool, Agent(instructions, "local-data-edge-cloud-clock")));

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, consume.Status, consume.Error);
    }

    [Test]
    public async Task ALocalAgent_FedByAKnowledgeBaseTool_Runs()
    {
        const string instructions = "local-data-edge-local";
        await using var harness = Host();

        var consume = await RunToTheConsumerAsync(harness, Graph(KnowledgeTool, Agent(instructions, GraphWorkflowModels.LocalDefault)));

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, consume.Status, consume.Error);
        AssertEx.Contains(Prompt(harness, instructions), "the fake tool answered");
    }

    /// <summary>An agent that does not inline its upstream sends none of it, so nothing local reaches the cloud model.</summary>
    [Test]
    public async Task ACloudAgent_ThatDoesNotIncludeUpstreamOutputs_Runs()
    {
        const string instructions = "local-data-edge-no-upstream";
        await using var harness = Host();

        var consume = await RunToTheConsumerAsync(harness, Graph(KnowledgeTool, Agent(instructions, "local-data-edge-cloud-no-upstream", includeUpstreamOutputs: false)));

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, consume.Status, consume.Error);
        AssertEx.False(Prompt(harness, instructions).Contains("the fake tool answered", StringComparison.Ordinal));
    }

    /// <summary>An LLM call carries upstream only through a binding: with one it is refused in its own words, with none it runs.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ACloudLlmCall_FedByAKnowledgeBaseTool_IsRefusedOnlyWhenItBindsInput(bool bound)
    {
        var prompt = $"local-data-edge-llm-{bound}";
        var model = $"local-data-edge-cloud-llm-{bound}";
        await using var harness = Host();

        var consume = await RunToTheConsumerAsync(harness, Graph(KnowledgeTool, LlmCall(prompt, model, bound)));

        if (bound)
        {
            AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, consume.FailureClass);
            var error = AssertEx.NotNull(consume.Error);
            AssertEx.Contains(error, $"to this LLM call on the cloud model '{model}'");
            AssertEx.Contains(error, "Tool node 'kb'");
            AssertEx.Contains(error, "grant local data on the model's external connection");
            AssertEx.False(Sent(harness, prompt), "a refused node builds no package.");
            AssertEx.Empty(Capacity(harness).ReservationsFor(model));
        }
        else
        {
            AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, consume.Status, consume.Error);
        }
    }

    /// <summary>
    ///     Taint follows SATISFIED edges: an <c>Any</c> join whose knowledge-base branch failed runs on the other branch,
    ///     because the failed Tool contributed nothing to its input.
    /// </summary>
    [Test]
    public async Task AnAnyJoin_WhoseKnowledgeBaseBranchFailed_RunsOnTheOtherBranch()
    {
        const string instructions = "local-data-edge-any-join";
        await using var harness = Host();
        Tools(harness).Script(KnowledgeTool, new GraphWorkflowScriptedTool
        {
            Kind = ToolInvocationOutcomeKind.Faulted,
            Reason = "the knowledge base could not answer"
        });

        var runId = await RunAsync(harness, AnyJoinGraph(Agent(instructions, "local-data-edge-cloud-any-join", nodeExtras: """, "joinPolicy": "Any" """)));

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, (await harness.ReadNodeRunAsync(runId, "kb")).Status, "the knowledge-base branch is the one that failed.");
        var consume = await harness.ReadNodeRunAsync(runId, "consume");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, consume.Status, consume.Error);
        AssertEx.Contains(Prompt(harness, instructions), "the fake tool answered", message: "the satisfied clock branch is what the agent read.");
    }

    /// <summary>One reason per row: with both switches off and no grant, the unattended refusal answers first.</summary>
    [Test]
    public async Task ACloudAgent_WithUnattendedRunsAndLocalDataBothWithheld_IsRefusedForUnattendedRuns()
    {
        const string instructions = "local-data-edge-both-off";
        const string model = "local-data-edge-cloud-both-off";
        await using var harness = Host(allowCloudModelUnattendedRuns: false);

        var consume = await RunToTheConsumerAsync(harness, Graph(KnowledgeTool, Agent(instructions, model)));

        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, consume.FailureClass);
        var error = AssertEx.NotNull(consume.Error);
        AssertEx.Contains(error, "Let cloud models run unattended", message: "the unattended gate runs before the local-data gate.");
        AssertEx.False(error.Contains("node-local data", StringComparison.Ordinal), error);
        AssertEx.False(Sent(harness, instructions), "a refused node builds no package.");
        AssertEx.Empty(Capacity(harness).ReservationsFor(model));
    }

    /// <summary>A private agent host with the scripted tool service, unattended cloud runs on unless a test says otherwise.</summary>
    private static GraphWorkflowHarness Host(bool allowCloudModelAccess = false, bool allowCloudModelUnattendedRuns = true)
    {
        var settings = StubNodeRuntimeSettings.Create().WithAllowCloudModelUnattendedRuns(allowCloudModelUnattendedRuns).WithAllowCloudModelAccess(allowCloudModelAccess).Build();
        var harness = GraphWorkflowHarness.PrivateAgentHost(services =>
        {
            GraphWorkflowAgentHostFixture.WithRuntimeSettings(settings)(services);
            services.RemoveAll<IToolInvocationService>();
            services.AddSingleton<IToolInvocationService, FakeGraphWorkflowToolInvocation>();
        });
        Tools(harness).Declare(KnowledgeTool);
        Tools(harness).Declare(ClockTool);
        return harness;
    }

    /// <summary>Runs the graph to its end and answers the consumer's row.</summary>
    private static async Task<GraphWorkflowNodeRunSnapshot> RunToTheConsumerAsync(GraphWorkflowHarness harness, string graphJson) =>
        await harness.ReadNodeRunAsync(await RunAsync(harness, graphJson), "consume");

    private static async Task<Guid> RunAsync(GraphWorkflowHarness harness, string graphJson)
    {
        var runId = await harness.StartRunAsync(graphJson);
        await harness.AdvanceUntilAsync(runId, async () => GraphWorkflowStateMachine.IsTerminal((await harness.ReadRunAsync(runId)).Status), "the run never settled");
        return runId;
    }

    /// <summary>Start → kb (Tool) → consume → End.</summary>
    private static string Graph(string toolName, string consumer) =>
        $$"""
          { "schemaVersion": 1,
            "nodes": [{ "key": "start", "kind": "Start" },
                      { "key": "kb", "kind": "Tool", "config": { "toolName": "{{toolName}}" } },
                      {{consumer}},
                      { "key": "done", "kind": "End", "config": { "outcome": "completed" } }],
            "edges": [{ "key": "e1", "from": "start", "to": "kb" },
                      { "key": "e2", "from": "kb", "to": "consume" },
                      { "key": "e3", "from": "consume", "to": "done" }] }
          """;

    /// <summary>Start → fanout → (kb | clock) → consume (Any) → End, with the knowledge-base Tool on one attempt.</summary>
    private static string AnyJoinGraph(string consumer) =>
        $$"""
          { "schemaVersion": 1,
            "nodes": [{ "key": "start", "kind": "Start" },
                      { "key": "fanout", "kind": "Parallel", "config": {} },
                      { "key": "kb", "kind": "Tool", "maxAttempts": 1, "config": { "toolName": "{{KnowledgeTool}}" } },
                      { "key": "clock", "kind": "Tool", "config": { "toolName": "{{ClockTool}}" } },
                      {{consumer}},
                      { "key": "done", "kind": "End", "config": { "outcome": "completed" } }],
            "edges": [{ "key": "e1", "from": "start", "to": "fanout" },
                      { "key": "e2", "from": "fanout", "to": "kb" },
                      { "key": "e3", "from": "fanout", "to": "clock" },
                      { "key": "e4", "from": "kb", "to": "consume" },
                      { "key": "e5", "from": "clock", "to": "consume" },
                      { "key": "e6", "from": "consume", "to": "done" }] }
          """;

    private static string Agent(string instructions, string model, bool includeUpstreamOutputs = true, string? nodeExtras = null) =>
        $$"""
          { "key": "consume", "kind": "Agent"{{nodeExtras}}, "config": { "instructions": "{{instructions}}", "model": "{{model}}", "includeUpstreamOutputs": {{(includeUpstreamOutputs ? "true" : "false")}} } }
          """;

    private static string LlmCall(string prompt, string model, bool bound) =>
        $$"""
          { "key": "consume", "kind": "LlmCall", "config": { "prompt": "{{prompt}}", "model": "{{model}}"{{(bound ? ", \"inputBindings\": { \"found\": \"upstream.kb\" }" : string.Empty)}} } }
          """;

    private static bool Sent(GraphWorkflowHarness harness, string fragment) =>
        harness.Invocations.Packages.Any(package => package.ConversationContext[0].Content.Contains(fragment, StringComparison.Ordinal));

    private static string Prompt(GraphWorkflowHarness harness, string instructions) =>
        harness.Invocations.PackageFor(instructions).ConversationContext[0].Content;

    private static FakeGraphWorkflowToolInvocation Tools(GraphWorkflowHarness harness) =>
        (FakeGraphWorkflowToolInvocation)harness.Services.GetRequiredService<IToolInvocationService>();

    private static FakeGraphWorkflowCapacity Capacity(GraphWorkflowHarness harness) =>
        (FakeGraphWorkflowCapacity)harness.Services.GetRequiredService<ICapacityService>();
}
