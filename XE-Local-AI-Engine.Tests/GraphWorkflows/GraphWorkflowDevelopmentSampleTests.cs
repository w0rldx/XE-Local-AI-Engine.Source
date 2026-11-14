namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The samples as the New workflow dialog posts them, saved and started on a host, and the <c>dev-intake</c> sample
///     run end to end over the scripted Development chain: only the model and the coder chain are faked.
/// </summary>
/// <remarks>Every test takes a host of its own: the scripted chain and the runner's scripts are container singletons.</remarks>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowDevelopmentSampleTests
{
    private const string ClassifyFragment = "What kind of development request";
    private const string PlanFragment = "Plan the change the operator's request asks for";
    private const string AnswerFragment = "Answer the operator's question about this repository";

    private const string Request = "Add a CONTRIBUTING.md naming the build and test commands.";

    // Plain words only: the bound data is JSON, where a quote reads \", so a substring check would miss one.
    private const string PlanText = "Feature. Add CONTRIBUTING.md at the root listing the make build and make test commands.";

    /// <summary>
    ///     Save is the parser (<c>DEVTASK-1</c> included), the lint and the service warnings, and none may speak; start is the
    ///     re-parse and the project binding, which accept every sample once its input names a project.
    /// </summary>
    [Test]
    public async Task EverySample_SavesCleanAndStartsOnAProject()
    {
        using var folder = new TempDirectory("xe-dev-sample");
        await using var harness = Host(folder.Path);
        var projectId = await GraphWorkflowDevTaskLaneTests.SeedProjectAsync(harness, folder.Path);
        var agentId = await SeedRepositoryAgentAsync(harness);

        foreach (var file in GraphWorkflowSampleContractTests.SampleFiles())
        {
            var name = Path.GetFileName(file);
            var graphJson = GraphWorkflowSampleContractTests.PostedGraphJson(file, agentId);
            Guid definitionId;
            await using (var scope = harness.Services.CreateAsyncScope())
            {
                var definitions = scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>();
                var validation = await definitions.ValidateAsync(graphJson);
                AssertEx.Empty(validation.Errors.Concat(validation.Warnings).Select(static entry => $"{entry.Key}: {entry.Message}"), $"{name} must validate clean.");
                definitionId = (await definitions.CreateAsync(name, description: null, graphJson)).Id;
            }

            var runId = await harness.StartRunOfAsync(definitionId, InputFor(file, projectId));
            AssertEx.Equal(GraphWorkflowRunStatus.Pending, (await harness.ReadRunAsync(runId)).Status, $"{name} must start.");
        }
    }

    /// <summary>The sample's own default input names no project, so the start refuses it with the sentence that says which key to fill in.</summary>
    [Test]
    public async Task TheIntakeSample_IsRefusedAtStartUntilItsInputNamesAProject()
    {
        using var folder = new TempDirectory("xe-dev-sample");
        await using var harness = Host(folder.Path);
        var definitionId = await harness.SeedDefinitionAsync(IntakeGraph(await SeedRepositoryAgentAsync(harness)));

        var refusal = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() => harness.StartRunOfAsync(definitionId));

        AssertEx.Contains(refusal.Message, "its run input needs 'developmentProjectId'");
    }

    /// <summary>
    ///     A change request: classified, planned, approved, implemented as ONE task from the plan, approved again and applied
    ///     by the run on its own behalf. No task before the first approval, no apply before the second.
    /// </summary>
    [Test]
    public async Task AChangeRequest_IsPlannedImplementedAndAppliedAfterTwoApprovals()
    {
        using var folder = new TempDirectory("xe-dev-sample");
        await using var harness = Host(folder.Path);
        var projectId = await GraphWorkflowDevTaskLaneTests.SeedProjectAsync(harness, folder.Path);
        var runId = await StartChangeAsync(harness, projectId);
        AssertEx.Contains(Prompt(harness, PlanFragment), Request, message: "the plan reads the request over its content edge from Start.");
        AssertEx.Equal(expected: 1, (await GraphWorkflowDevTaskLaneTests.ListTasksAsync(harness, projectId)).Count, "no task exists before the plan is approved.");
        AssertEx.Empty(Chain(harness).Actions);

        var taskId = await ApprovePlanAsync(harness, runId);
        var task = await GraphWorkflowDevTaskLaneTests.ReadTaskAsync(harness, taskId);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, task.Status);
        AssertEx.Contains(task.Requirements, PlanText, message: "the task is built from the approved plan.");
        AssertEx.Contains(task.Requirements, Request, message: "and carries the request beside it.");
        AssertEx.Empty(Chain(harness).Offered, "nothing is applied before the result is approved.");

        _ = await harness.DecideAsync(runId, "resultapproval", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve);
        await SettleAsync(harness, runId);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "apply")).Status);
        AssertEx.Equal(DevelopmentTaskStatus.Completed, (await GraphWorkflowDevTaskLaneTests.ReadTaskAsync(harness, taskId)).Status, "the task is applied.");
        AssertEx.Equal(new DevelopmentWorkflowRunReference
            {
                Engine = DevelopmentWorkflowEngine.GraphWorkflow,
                RunId = runId
            },
            Chain(harness).Callers.Single(),
            "the run applied it on its own behalf.");
        await AssertEndedAtAsync(harness, runId, "end_applied");
    }

    /// <summary>A Reject at the result approval ends the run on its own End: the task stays awaiting apply and the gate is never asked.</summary>
    [Test]
    public async Task ARejectAtTheResultApproval_EndsTheRunWithoutApplying()
    {
        using var folder = new TempDirectory("xe-dev-sample");
        await using var harness = Host(folder.Path);
        var runId = await StartChangeAsync(harness, await GraphWorkflowDevTaskLaneTests.SeedProjectAsync(harness, folder.Path));
        var taskId = await ApprovePlanAsync(harness, runId);

        _ = await harness.DecideAsync(runId, "resultapproval", Guid.NewGuid(), GraphWorkflowDecisionKind.Reject, comment: "Not this way.");
        await SettleAsync(harness, runId);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, "apply")).Status);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, (await GraphWorkflowDevTaskLaneTests.ReadTaskAsync(harness, taskId)).Status, "the task is left for the Development page.");
        AssertEx.Empty(Chain(harness).Offered, "nothing was offered to the apply gate.");
        AssertEx.Empty(Chain(harness).Callers, "and nothing asked it.");
        await AssertEndedAtAsync(harness, runId, "end_not_applied");
    }

    /// <summary>A question is answered from the repository and ends there: no plan, no task, nothing asked of Development Mode.</summary>
    [Test]
    public async Task AQuestion_IsAnsweredAndCreatesNoTask()
    {
        using var folder = new TempDirectory("xe-dev-sample");
        await using var harness = Host(folder.Path);
        var projectId = await GraphWorkflowDevTaskLaneTests.SeedProjectAsync(harness, folder.Path);
        harness.Invocations.Script(ClassifyFragment, new GraphWorkflowScriptedTurn
        {
            Text = """{"choice":"question"}"""
        });
        harness.Invocations.Script(AnswerFragment, new GraphWorkflowScriptedTurn
        {
            Text = "It builds with make."
        });
        var definitionId = await harness.SeedDefinitionAsync(IntakeGraph(await SeedRepositoryAgentAsync(harness)));
        var runId = await harness.StartRunOfAsync(definitionId, Input(projectId));

        await SettleAsync(harness, runId);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "answer")).Status);
        AssertEx.Contains(Prompt(harness, AnswerFragment), Request, message: "the answer reads the request over its content edge from Start.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, "plan")).Status);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, "implement")).Status);
        AssertEx.Equal(expected: 1, (await GraphWorkflowDevTaskLaneTests.ListTasksAsync(harness, projectId)).Count, "no task was created.");
        AssertEx.Empty(Chain(harness).Actions, "nothing was asked of Development Mode.");
        await AssertEndedAtAsync(harness, runId, "end_answered");
    }

    /// <summary>Starts <c>dev-intake</c> on a change request and ticks it until the plan approval parks.</summary>
    private static async Task<Guid> StartChangeAsync(GraphWorkflowHarness harness, Guid projectId)
    {
        harness.Invocations.Script(ClassifyFragment, new GraphWorkflowScriptedTurn
        {
            Text = """{"choice":"change"}"""
        });
        harness.Invocations.Script(PlanFragment, new GraphWorkflowScriptedTurn
        {
            Text = PlanText
        });
        var definitionId = await harness.SeedDefinitionAsync(IntakeGraph(await SeedRepositoryAgentAsync(harness)));
        var runId = await harness.StartRunOfAsync(definitionId, Input(projectId));
        await ParksAtAsync(harness, runId, "planapproval");
        return runId;
    }

    /// <summary>Approves the plan and ticks until the result approval parks; answers the task the implement node drove.</summary>
    private static async Task<Guid> ApprovePlanAsync(GraphWorkflowHarness harness, Guid runId)
    {
        _ = await harness.DecideAsync(runId, "planapproval", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve);
        await ParksAtAsync(harness, runId, "resultapproval");
        var implement = await harness.ReadNodeRunAsync(runId, "implement");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, implement.Status, implement.Error ?? implement.OutputJson);
        return implement.DevelopmentTaskId ?? throw new AssertionException("The implement node names no Development task.");
    }

    private static Task ParksAtAsync(GraphWorkflowHarness harness, Guid runId, string pauseKey) =>
        harness.AdvanceUntilAsync(runId,
            async () => (await harness.ReadNodeRunAsync(runId, pauseKey)).Status == GraphWorkflowNodeRunStatus.WaitingForApproval,
            $"the run never parked at '{pauseKey}'");

    private static Task SettleAsync(GraphWorkflowHarness harness, Guid runId) =>
        harness.AdvanceUntilAsync(runId,
            async () => GraphWorkflowStateMachine.IsTerminal((await harness.ReadRunAsync(runId)).Status),
            "the run never ended");

    /// <summary>The run completed through <paramref name="endKey" /> and through no other End.</summary>
    private static async Task AssertEndedAtAsync(GraphWorkflowHarness harness, Guid runId, string endKey)
    {
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId)).Status);
        var ended = (await harness.ReadNodeRunsAsync(runId))
                    .Where(static nodeRun => nodeRun.NodeKey.StartsWith("end_", StringComparison.Ordinal) && nodeRun.Status == GraphWorkflowNodeRunStatus.Succeeded)
                    .Select(static nodeRun => nodeRun.NodeKey);
        AssertEx.Equal(endKey, string.Join(", ", ended));
    }

    private static string Prompt(GraphWorkflowHarness harness, string fragment) =>
        harness.Invocations.PackageFor(fragment).ConversationContext[0].Content;

    private static FakeDevelopmentTaskChain Chain(GraphWorkflowHarness harness) =>
        (FakeDevelopmentTaskChain)harness.Services.GetRequiredService<IDevelopmentManagementService>();

    private static string IntakeGraph(Guid agentId) =>
        GraphWorkflowSampleContractTests.PostedGraphJson(GraphWorkflowSampleContractTests.SampleFiles().Single(static file => Path.GetFileName(file) == "dev-intake.json"), agentId);

    private static string Input(Guid projectId) =>
        JsonSerializer.Serialize(new
        {
            request = Request,
            developmentProjectId = projectId
        });

    /// <summary>The sample's own Start <c>defaultInput</c>, with the project filled in as an operator would in the start dialog.</summary>
    private static string InputFor(string file, Guid projectId)
    {
        var start = AssertEx.NotNull(JsonNode.Parse(File.ReadAllText(file)))["nodes"]!.AsArray().Single(static node => (string?)node!["kind"] == "Start")!;
        var input = AssertEx.NotNull(start["config"]?["defaultInput"]?.DeepClone().AsObject(), "every sample carries a default input.");
        input["developmentProjectId"] = projectId.ToString("D");
        return input.ToJsonString();
    }

    /// <summary>A saved agent listing the three repository reads, as the seeded "Coder (read-only)" does.</summary>
    private static async Task<Guid> SeedRepositoryAgentAsync(GraphWorkflowHarness harness)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var definition = await scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>()
                                    .AddAsync(new AgentDefinitionInput
                                    {
                                        Name = $"Repository reader {Guid.NewGuid():N}",
                                        Description = null,
                                        Instructions = "Read the repository.",
                                        ModelProfile = null,
                                        ReasoningEffort = null,
                                        Kind = AgentDefinitionKind.Single,
                                        AllowedToolNames = [CoderToolDefinition.ListFilesToolName, CoderToolDefinition.ReadFileToolName, CoderToolDefinition.SearchTextToolName],
                                        ToolApprovals = new Dictionary<string, bool>(StringComparer.Ordinal),
                                        OrchestrationTopologyJson = null
                                    });
        return definition.Id;
    }

    private static GraphWorkflowHarness Host(string repositoryRoot) =>
        GraphWorkflowHarness.PrivateAgentHost(services =>
        {
            services.RemoveAll<IDevelopmentManagementService>();
            services.AddSingleton<IDevelopmentManagementService>(static provider => new FakeDevelopmentTaskChain(provider.GetRequiredService<IServiceScopeFactory>()));
            GraphWorkflowDevTaskLaneTests.AcceptEveryProject(services, repositoryRoot);
        });
}
