namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class McpAgentRunExecutorTests
{
    [Test]
    public async Task ExecuteAsync_DuringExecution_EstablishesRootSpawnContext()
    {
        var execution = Substitute.For<IMcpAgentExecutionService>();
        SpawnContext? observedContext = null;
        execution.SpawnForMcpAsync(Arg.Any<McpExecutionBindingRequest>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                observedContext = SpawnContext.Current;
                return Task.FromResult(SpawnOutcome.Success("complete"));
            });
        var executor = new McpAgentRunExecutor(execution,
            Options.Create(new SpawnOptions { MaxConcurrentSpawns = 2, MaxCloudSpawns = 1 }));

        _ = await executor.ExecuteAsync(CreateRun(), CancellationToken.None).ConfigureAwait(false);

        AssertEx.NotNull(observedContext);
        AssertEx.Equal(expected: 0, observedContext!.Depth);
    }

    [Test]
    public async Task ExecuteAsync_AfterExecution_ReturnsOutcomeAndRestoresAmbientContext()
    {
        var execution = Substitute.For<IMcpAgentExecutionService>();
        var expected = SpawnOutcome.Success("detached result");
        execution.SpawnForMcpAsync(Arg.Any<McpExecutionBindingRequest>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(expected);
        var executor = new McpAgentRunExecutor(execution, Options.Create(new SpawnOptions()));

        var outcome = await executor.ExecuteAsync(CreateRun(), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected, outcome);
        AssertEx.Null(SpawnContext.Current,
            "The executor returns only its execution outcome; the dispatcher owns terminal persistence after this scope exits.");
    }

    [Test]
    public async Task ExecuteAsync_WhenRunUsesSavedAgent_ReconstructsAcceptedBindingSnapshot()
    {
        var execution = Substitute.For<IMcpAgentExecutionService>();
        McpExecutionBindingRequest? captured = null;
        string? fingerprint = null;
        execution.SpawnForMcpAsync(Arg.Any<McpExecutionBindingRequest>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<McpExecutionBindingRequest>();
                fingerprint = callInfo.ArgAt<string?>(2);
                return Task.FromResult(SpawnOutcome.Success("complete"));
            });
        var executor = new McpAgentRunExecutor(execution, Options.Create(new SpawnOptions()));
        var run = CreateRun() with
        {
            AgentDefinitionId = Guid.Parse("8fd3bb15-eafb-4e34-bb88-11b9fa6deae3"),
            ModelOverrideId = "override-model"
        };

        _ = await executor.ExecuteAsync(run, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(run.AgentDefinitionId.Value.ToString("D"), captured!.AgentKey);
        AssertEx.Equal("override-model", captured.ModelOverrideId);
        AssertEx.Equal(run.Instructions!, captured.Instructions);
        AssertEx.Null(captured.ModelId);
        AssertEx.Equal(Convert.ToHexString(run.BindingFingerprint!.Value.Span), fingerprint);
    }

    [Test]
    public async Task ExecuteAsync_WhenClaimPayloadIsMissing_FailsClosedWithoutInvokingAgent()
    {
        var execution = Substitute.For<IMcpAgentExecutionService>();
        var executor = new McpAgentRunExecutor(execution, Options.Create(new SpawnOptions()));

        var outcome = await executor.ExecuteAsync(CreateRun() with
        {
            ModelId = null,
            BindingFingerprint = null,
            Task = null,
            PayloadExpired = true
        }, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(SpawnOutcomeKind.Failed, outcome.Kind);
        AssertEx.Equal(McpExecutionFailureCodes.AgentConfigChanged, outcome.FailureCode!);
        await execution.DidNotReceiveWithAnyArgs().SpawnForMcpAsync(default!, default!, default, default);
    }

    [Test]
    public async Task ExecuteAsync_WithWorkspace_AwaitsExecutionReleaseAndForwardsOpaqueWorkspaceId()
    {
        var execution = Substitute.For<IMcpAgentExecutionService>();
        var released = new TaskCompletionSource<SpawnOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid? capturedWorkspaceId = null;
        execution.SpawnForMcpAsync(Arg.Any<McpExecutionBindingRequest>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<Guid?>())
            .Returns(callInfo =>
            {
                capturedWorkspaceId = callInfo.ArgAt<Guid?>(4);
                return released.Task;
            });
        var executor = new McpAgentRunExecutor(execution, Options.Create(new SpawnOptions()));
        var workspaceId = Guid.NewGuid();

        var pending = executor.ExecuteAsync(CreateRun() with { WorkspaceId = workspaceId }, CancellationToken.None);

        AssertEx.False(pending.IsCompleted, "durable execution must not return while the workspace execution service still owns its lease.");
        released.SetResult(SpawnOutcome.Success("released"));
        var outcome = await pending.ConfigureAwait(false);
        AssertEx.Equal(SpawnOutcomeKind.Success, outcome.Kind);
        AssertEx.Equal(workspaceId, capturedWorkspaceId!.Value);
    }

    private static McpAgentRunRecord CreateRun() =>
        new(Guid.Parse("ae401853-c218-4c33-bae9-b9fc6fbd5c58"),
            SHA256.HashData("request"u8),
            McpAgentRunStatus.Running,
            Version: 1,
            ClaimToken: Guid.Parse("d76e095e-15de-49d3-887c-e19e9db7528d"),
            McpAgentRunStopReason.None,
            StopRequestedAtUtc: null,
            AgentDefinitionId: null,
            AgentDefinitionVersion: null,
            ModelId: "unsloth/Ornith-1.0-9B-GGUF:Q4_K_M",
            ModelOverrideId: null,
            WorkspaceId: null,
            BindingFingerprint: SHA256.HashData("binding"u8),
            Task: "inspect the repository",
            Instructions: "read only",
            Result: null,
            DisplayMessage: null,
            FailureCode: null,
            CreatedAtUtc: 1,
            ClaimedAtUtc: 2,
            CompletedAtUtc: null,
            PayloadExpiresAtUtc: 86_400_001,
            CompactedAtUtc: null,
            PayloadExpired: false);
}
