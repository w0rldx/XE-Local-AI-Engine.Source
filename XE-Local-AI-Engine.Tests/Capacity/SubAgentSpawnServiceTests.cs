namespace XE_Local_AI_Engine.Tests.Capacity;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="SubAgentSpawnService" /> (Lane B) tests: the spawn dispatcher. An admitted local spawn builds a
///     tool-less sub-agent and returns its result while disposing the ledger reservation; a same-model verdict
///     serializes (no second load) with a bounded wait that rejects on timeout; an over-cap fan-out or cloud-spawn is
///     rejected; a reject verdict returns its sanitized reason as the result (no exception, no path/secret); and the
///     outer cancellation token flows into the inner run. The capacity verdict is mocked so each dispatch branch is
///     exercised in isolation; no Ollama/Docker/network.
/// </summary>
public sealed class SubAgentSpawnServiceTests
{
    private const string Model = "bartowski/Model-GGUF:Q4_K_M";

    [Test]
    public async Task Spawn_WhenAllow_BuildsBoundSubAgent_AndReturnsResult()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(ModelRequest("do the thing"), CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        AssertEx.Equal(1, harness.ChatClient.CallCount);
        // The child run MUST carry the bound model id so RuntimeChatClient routes the shared client to the right
        // provider; without it the inner run falls back to the node default provider (a real bug a mocked-out ModelId
        // would hide). The model id rides ChatOptions.ModelId, set on the child's ChatClientAgentOptions.ChatOptions.
        AssertEx.Equal(Model, harness.ChatClient.LastModelId);
        // The local Allow reservation must be released when the child exits.
        AssertEx.True(harness.ReservationDisposed, "ledger reservation should be disposed on child exit");
    }

    [Test]
    public async Task Spawn_WhenAllow_ChildInheritsProfileTools_ButNotSpawnSubAgent()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        // The profile lists spawn_subagent ALONGSIDE real tools — it must be filtered from the child set regardless.
        var id = harness.RegisterProfile("coder", "read_file", "list_files", "spawn_subagent");
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(new SubAgentSpawnRequest { SubAgentKey = id.ToString(), Task = "read it" }, CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        // The child inherited its profile's curated tools…
        AssertEx.Contains(harness.ChatClient.LastToolNames, "read_file");
        AssertEx.Contains(harness.ChatClient.LastToolNames, "list_files");
        // …but spawn_subagent is UNCONDITIONALLY filtered out (structural depth cap), even though the profile listed it.
        AssertEx.False(harness.ChatClient.LastToolNames.Any(name => name == "spawn_subagent"),
            "the child tool set must never contain spawn_subagent");
    }

    [Test]
    public async Task Spawn_WhenModelIdOnly_ChildIsToolLess()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(ModelRequest("ad-hoc"), CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        // A model-id-only spawn has no agent profile, so no AllowedToolNames → the child runs tool-less.
        AssertEx.Empty(harness.ChatClient.LastToolNames);
    }

    [Test]
    public async Task Spawn_WhenDepthGuardHit_Rejects()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        // Simulate a child-depth context (Depth >= 1): the runtime depth guard rejects with a sanitized reason and the
        // inner agent never runs. (In production a Depth-1 agent is also tool-less, so it cannot reach here at all.)
        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        using var child = SpawnContext.Current!.BeginChildScope();

        var result = await service.SpawnAsync(ModelRequest("nested"), CancellationToken.None);

        AssertEx.Contains(result, "may not spawn further sub-agents");
        AssertEx.Equal(0, harness.ChatClient.CallCount);
    }

    [Test]
    public async Task Spawn_PropagatesCancellationToInnerRun()
    {
        // A live cancellation while the inner agent is running must cancel the inner run (AsAIFunction forwards the ct).
        using var harness = new Harness(delayBeforeResponse: TimeSpan.FromSeconds(5));
        harness.AllowLocal();
        var service = harness.Build();

        using var cts = new CancellationTokenSource();
        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);

        var spawnTask = service.SpawnAsync(ModelRequest("slow task"), cts.Token);
        await Task.Delay(100);
        await cts.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => spawnTask);
        AssertEx.True(harness.ChatClient.InnerObservedCancellation, "inner run should observe the parent cancellation");
        // A cancelled spawn still releases its reservation.
        AssertEx.True(harness.ReservationDisposed, "reservation must be released even when the inner run is cancelled");
    }

    [Test]
    public async Task Spawn_WhenFanOutExceedsCap_Rejects()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        // A root with a fan-out cap of 1: hold one live spawn open, then a second concurrent spawn must be rejected.
        using var root = SpawnContext.BeginRoot(fanOutCap: 1, cloudSpawnCap: 3);
        var gate = new TaskCompletionSource();
        harness.ChatClient.HoldUntil(gate.Task);

        var first = service.SpawnAsync(ModelRequest("first"), CancellationToken.None);
        await harness.ChatClient.WaitUntilRunningAsync();

        var secondResult = await service.SpawnAsync(ModelRequest("second"), CancellationToken.None);
        AssertEx.Contains(secondResult, "concurrent sub-agents");

        gate.SetResult();
        await first;
    }

    [Test]
    public async Task Spawn_WhenCloudSpawnCapExceeded_Rejects()
    {
        // Cloud Allow carries a NULL reservation; the cloud-spawn cap is the gate. Cap = 1 → the 2nd cloud spawn rejects.
        using var harness = new Harness();
        harness.AllowCloud();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 1);

        var first = await service.SpawnAsync(ModelRequest("first"), CancellationToken.None);
        AssertEx.Equal("sub-agent-result", first);

        var second = await service.SpawnAsync(ModelRequest("second"), CancellationToken.None);
        AssertEx.Contains(second, "cloud sub-agents");
    }

    [Test]
    public async Task Spawn_WhenReject_ReturnsStructuredReason_NotException()
    {
        using var harness = new Harness();
        harness.RejectInsufficient();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(ModelRequest("nope"), CancellationToken.None);

        // The sanitized reason is returned as the tool result; the inner agent never runs.
        AssertEx.Contains(result, "Insufficient capacity");
        AssertEx.Equal(0, harness.ChatClient.CallCount);
        // No node-internal detail leaks: the model id / path must not appear in the caller-facing reason.
        AssertEx.False(result.Contains(Model, StringComparison.Ordinal), "reject reason must not leak the model id");
    }

    [Test]
    public async Task Spawn_WhenQueueSameModel_Serializes_NoSecondLoad()
    {
        using var harness = new Harness();
        harness.QueueSameModel();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(ModelRequest("queued"), CancellationToken.None);

        // The queued spawn runs the inner agent against the already-running process (no reservation, no second load).
        AssertEx.Equal("sub-agent-result", result);
        AssertEx.Equal(1, harness.ChatClient.CallCount);
        AssertEx.False(harness.ReservationDisposed, "QueueSameModel must not take a ledger reservation");
    }

    [Test]
    public async Task Spawn_WhenMissingSpawnContext_Rejects()
    {
        // With NO ambient SpawnContext (no root seeded), a spawn defaults safe — it is rejected rather than overrunning.
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        var result = await service.SpawnAsync(ModelRequest("orphan"), CancellationToken.None);

        AssertEx.Contains(result, "concurrent sub-agents");
        AssertEx.Equal(0, harness.ChatClient.CallCount);
    }

    [Test]
    public async Task Spawn_WhenInvalidArguments_Rejects()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);

        // Both bindings present (not mutually exclusive) → invalid.
        var bothResult = await service.SpawnAsync(
            new SubAgentSpawnRequest { ModelId = Model, SubAgentKey = "x", Task = "t" },
            CancellationToken.None);
        AssertEx.Contains(bothResult, "exactly one");

        // Empty task → invalid.
        var emptyTask = await service.SpawnAsync(
            new SubAgentSpawnRequest { ModelId = Model, Task = "   " },
            CancellationToken.None);
        AssertEx.Contains(emptyTask, "non-empty task");

        AssertEx.Equal(0, harness.ChatClient.CallCount);
    }

    private static SubAgentSpawnRequest ModelRequest(string task)
    {
        return new SubAgentSpawnRequest { ModelId = Model, Task = task };
    }

    // Assembles the spawn service over a mocked capacity verdict + a real SpawnSerializer + a gateable RecordingChatClient.
    private sealed class Harness : IDisposable
    {
        private readonly GateableChatClient _chatClient;
        private readonly ICapacityService _capacity = Substitute.For<ICapacityService>();
        private readonly IAgentDefinitionResolver _resolver = Substitute.For<IAgentDefinitionResolver>();
        private readonly IAgentDefinitionStore _definitionStore = Substitute.For<IAgentDefinitionStore>();
        private readonly IAgentToolRegistry _toolRegistry = new FakeAgentToolRegistry();
        private bool _reservationDisposed;

        public Harness(TimeSpan? delayBeforeResponse = null)
        {
            _chatClient = new GateableChatClient(delayBeforeResponse: delayBeforeResponse);
        }

        public GateableChatClient ChatClient => _chatClient;

        public bool ReservationDisposed => _reservationDisposed;

        // Registers a profile (subAgentKey → definition) whose resolved AllowedTools are the supplied offer names, so the
        // service curates the child tool set through the real InvocationToolResolver against the fake catalog.
        public Guid RegisterProfile(string name, params string[] allowedToolNames)
        {
            var id = Guid.NewGuid();
            var definition = new AgentDefinitionRecord(id,
                name,
                Description: null,
                Instructions: "child instructions",
                ModelProfile: Model,
                ReasoningEffort: null,
                Kind: AgentDefinitionKind.Single,
                AllowedToolNames: allowedToolNames,
                ToolApprovals: new Dictionary<string, bool>(StringComparer.Ordinal),
                OrchestrationTopologyJson: null,
                Version: 1,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0);
            _definitionStore.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(definition);

            var offered = allowedToolNames
                          .Select(static toolName => new AllowedToolDto
                          {
                              Id = Guid.NewGuid(),
                              Name = toolName,
                              Location = ToolLocation.ClientLocal,
                              ParameterSchema = "{\"type\":\"object\"}",
                              RequiresApproval = false
                          })
                          .ToArray();
            _resolver.ResolveAsync(id, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                     .Returns(new ResolvedAgentRuntime("child instructions", offered, Model, null, 1, id, name));
            return id;
        }

        public void AllowLocal()
        {
            // Ownership of the reservation transfers to the SUT, which disposes it on child exit (the assertion the
            // tests make). The analyzer cannot see the transfer through the mocked decision, so suppress here.
#pragma warning disable CA2000
            var reservation = new TrackingDisposable(() => _reservationDisposed = true);
#pragma warning restore CA2000
            _capacity.DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                     .Returns(_ => new CapacityDecision(CapacityVerdict.Allow, "Capacity available.", OllamaEvictionWarning: false, reservation));
        }

        public void AllowCloud()
        {
            // Cloud Allow carries a null reservation (no local cost).
            _capacity.DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                     .Returns(_ => new CapacityDecision(CapacityVerdict.Allow, "Cloud provider selected; no local capacity required.", OllamaEvictionWarning: false));
        }

        public void QueueSameModel()
        {
            _capacity.DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                     .Returns(_ => new CapacityDecision(CapacityVerdict.QueueSameModel, "Model already running; the spawn will share that process.", OllamaEvictionWarning: false));
        }

        public void RejectInsufficient()
        {
            _capacity.DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                     .Returns(_ => new CapacityDecision(CapacityVerdict.RejectInsufficient, "Insufficient capacity: not enough free memory for another model.", OllamaEvictionWarning: false));
        }

        public SubAgentSpawnService Build()
        {
            return new SubAgentSpawnService(_capacity,
                new SpawnSerializer(),
                _definitionStore,
                _resolver,
                _toolRegistry,
                new EmptyClientLocalToolRegistry(),
                new EmptyMcpToolRegistry(),
                _chatClient,
                Options.Create(new SpawnOptions { QueueWaitSeconds = 5 }),
                NullLoggerFactory.Instance,
                NullLogger<SubAgentSpawnService>.Instance);
        }

        public void Dispose()
        {
            _chatClient.Dispose();
        }
    }

    // A catalog that resolves the curated child tool names. Includes spawn_subagent so a test can prove it is filtered
    // out of the child set even when the profile lists it.
    private sealed class FakeAgentToolRegistry : IAgentToolRegistry
    {
        private static readonly IReadOnlyList<AITool> Tools =
        [
            AIFunctionFactory.Create((string input) => input, "spawn_subagent"),
            AIFunctionFactory.Create((string input) => input, "read_file"),
            AIFunctionFactory.Create((string input) => input, "list_files")
        ];

        public IReadOnlyList<AITool> GetLocalChatTools()
        {
            return Tools;
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors()
        {
            return [];
        }
    }

    private sealed class EmptyClientLocalToolRegistry : IClientLocalToolRegistry
    {
        public bool TryResolve(string toolName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AITool? tool)
        {
            tool = null;
            return false;
        }
    }

    private sealed class EmptyMcpToolRegistry : IMcpToolRegistry
    {
        public bool TryResolve(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AITool? tool)
        {
            tool = null;
            return false;
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetDescriptors()
        {
            return [];
        }

        public void ReplaceSnapshot(IReadOnlyList<McpRegisteredTool> tools)
        {
            // Not used by the spawn tests.
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        private readonly Action _onDispose;
        private int _disposed;

        public TrackingDisposable(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _onDispose();
            }
        }
    }
}
