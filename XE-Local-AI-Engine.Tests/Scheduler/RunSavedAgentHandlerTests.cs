namespace XE_Local_AI_Engine.Tests.Scheduler;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="RunSavedAgentHandler" /> (OPP-02) tests: parameter validation rejects a blank/invalid agent id or
///     prompt WITHOUT invoking the runner, a valid node-local agent is run through the shared invocation runner with its
///     resolved system prompt and the effective model bound as <c>ModelProfile</c>, a cloud-effective agent is rejected
///     UP FRONT (no run), a missing agent throws a sanitized <see cref="ScheduledJobExecutionException" />, the capacity
///     reservation is disposed on both success and failure, approval-required tools are stripped from the unattended
///     offer, an <see cref="OperationCanceledException" /> propagates, and the recorded run summary is content-safe.
/// </summary>
public sealed class RunSavedAgentHandlerTests
{
    private const string AgentIdString = "11111111-1111-1111-1111-111111111111";
    private const string EffectiveLocalModel = "local-default-model";
    private const string Prompt = "Summarize the overnight error logs.";

    private static readonly Guid AgentId = Guid.Parse(AgentIdString);

    [Test]
    public void Descriptor_ClaimsReservedTemplateIdAndAllowsAgentCreation()
    {
        using var harness = new Harness();

        AssertEx.Equal("run-agent", harness.Handler.TemplateId);
        AssertEx.Equal("run-agent", harness.Handler.Descriptor.TemplateId);
        AssertEx.True(harness.Handler.Descriptor.AllowAgentCreation, "OPP-02 lets the AI agent schedule saved-agent runs.");
        AssertEx.True(harness.Handler.Descriptor.AllowManualTrigger, "operators may run a scheduled agent on demand.");
        AssertEx.Equal(SchedulerMisfirePolicy.SkipMissed, harness.Handler.Descriptor.DefaultMisfirePolicy);
        AssertEx.Equal(expected: 600, harness.Handler.Descriptor.DefaultMaxRuntimeSeconds ?? 0);
        AssertEx.Equal(HistoryDetailLevel.Detailed, harness.Handler.Descriptor.HistoryDetailLevel);
        AssertEx.NotNull(harness.Handler.Descriptor.ParameterSchema);
    }

    [Test]
    [Arguments("")]
    [Arguments("not json")]
    [Arguments("""{ "prompt": "hi" }""")]
    [Arguments("""{ "agentDefinitionId": "", "prompt": "hi" }""")]
    [Arguments("""{ "agentDefinitionId": "not-a-guid", "prompt": "hi" }""")]
    [Arguments("""{ "agentDefinitionId": "00000000-0000-0000-0000-000000000000", "prompt": "hi" }""")]
    [Arguments("""{ "agentDefinitionId": "11111111-1111-1111-1111-111111111111" }""")]
    [Arguments("""{ "agentDefinitionId": "11111111-1111-1111-1111-111111111111", "prompt": "   " }""")]
    public async Task ExecuteAsync_WhenParametersInvalid_ThrowsValidationExceptionWithoutRunning(string parametersJson)
    {
        using var harness = new Harness();

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => harness.Handler.ExecuteAsync(Context(parametersJson), CancellationToken.None));

        AssertEx.Equal(expected: 0, harness.RunCount);
    }

    [Test]
    public async Task ExecuteAsync_WhenValidLocalAgent_RunsResolvedRuntimeWithBoundModel()
    {
        using var harness = new Harness();

        await harness.Handler.ExecuteAsync(Context(ValidParams()), CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.RunCount);
        AssertEx.NotNull(harness.CapturedPackage);
        // The effective model (no pin => the node local default) is bound as ModelProfile so the runner never silently
        // falls back to the node default.
        AssertEx.Equal(EffectiveLocalModel, harness.CapturedPackage!.ModelProfile!);
        // The COMPLETE resolved runtime prompt is used (scaffold + persona + folded memory), NOT the raw instructions.
        AssertEx.Equal("SCAFFOLD+PERSONA", harness.CapturedPackage.ResolvedSystemPrompt);
        AssertEx.Equal(expected: 7, harness.CapturedPackage.AgentDefinitionVersion);
        // The prompt is delivered as the single seed user turn.
        AssertEx.Equal(expected: 1, harness.CapturedPackage.ConversationContext.Count);
        AssertEx.Equal(MessageRole.User, harness.CapturedPackage.ConversationContext[0].Role);
        AssertEx.Equal(Prompt, harness.CapturedPackage.ConversationContext[0].Content);
    }

    [Test]
    public async Task ExecuteAsync_WhenAgentPinsLocalModel_BindsThePinnedModel()
    {
        using var harness = new Harness();
        harness.Store.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(BuildDefinition(modelProfile: "pinned-local-model"));
        harness.Resolver
               .ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
               .Returns(new ResolvedAgentRuntime("SCAFFOLD+PERSONA", [], "pinned-local-model", null, 7, AgentId, "Pinned Agent", []));

        await harness.Handler.ExecuteAsync(Context(ValidParams()), CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.RunCount);
        AssertEx.Equal("pinned-local-model", harness.CapturedPackage!.ModelProfile!);
    }

    [Test]
    public async Task ExecuteAsync_WhenEffectiveModelIsCloud_RejectsUpFrontWithoutRunning()
    {
        using var harness = new Harness();
        harness.Store.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(BuildDefinition(modelProfile: "azure-gpt"));
        harness.Capability.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns((SupportsThinking: true, SupportsTools: true, IsCloud: true));

        await AssertEx.ThrowsAsync<ScheduledJobExecutionException>(() => harness.Handler.ExecuteAsync(Context(ValidParams()), CancellationToken.None));

        AssertEx.Equal(expected: 0, harness.RunCount);
        // The cloud gate fires before capacity admission and before any resolve.
        await harness.Capacity.DidNotReceive().DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WhenAgentMissing_ThrowsSanitizedExecutionExceptionWithoutRunning()
    {
        using var harness = new Harness();
        harness.Store.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AgentDefinitionRecord?)null);

        var exception = await AssertEx.ThrowsAsync<ScheduledJobExecutionException>(() => harness.Handler.ExecuteAsync(Context(ValidParams()), CancellationToken.None));

        AssertEx.Contains(exception.Message, "could not be found");
        AssertEx.Equal(expected: 0, harness.RunCount);
    }

    [Test]
    public async Task ExecuteAsync_WhenCapacityRejects_ThrowsSanitizedReasonWithoutRunning()
    {
        using var harness = new Harness();
        harness.Capacity
               .DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
               .Returns(new CapacityDecision(CapacityVerdict.RejectInsufficient, "Insufficient capacity: not enough free memory for another model.", OllamaEvictionWarning: false));

        var exception = await AssertEx.ThrowsAsync<ScheduledJobExecutionException>(() => harness.Handler.ExecuteAsync(Context(ValidParams()), CancellationToken.None));

        AssertEx.Contains(exception.Message, "Insufficient capacity");
        AssertEx.Equal(expected: 0, harness.RunCount);
        AssertEx.False(harness.ReservationDisposed, "a reject carries no reservation to dispose.");
    }

    [Test]
    public async Task ExecuteAsync_OnSuccess_DisposesCapacityReservation()
    {
        using var harness = new Harness();

        await harness.Handler.ExecuteAsync(Context(ValidParams()), CancellationToken.None);

        AssertEx.True(harness.ReservationDisposed, "the ledger reservation must be released when the run completes.");
    }

    [Test]
    public async Task ExecuteAsync_WhenRunnerThrows_DisposesCapacityReservationAndPropagates()
    {
        using var harness = new Harness();
        harness.Runner
               .When(runner => runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>()))
               .Do(_ => throw new InvalidOperationException("runner blew up"));

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => harness.Handler.ExecuteAsync(Context(ValidParams()), CancellationToken.None));

        AssertEx.True(harness.ReservationDisposed, "the reservation must be released even when the run faults.");
    }

    [Test]
    public async Task ExecuteAsync_WhenCancelled_PropagatesOperationCanceledAndDisposesReservation()
    {
        using var harness = new Harness();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // The real runner swallows OCE (it reports Cancelled to the dispatcher and returns); the handler re-surfaces the
        // cancellation so the dispatcher records Cancelled/TimedOut. The fake runner returning normally models the swallow.
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => harness.Handler.ExecuteAsync(Context(ValidParams()), cts.Token));

        AssertEx.True(harness.ReservationDisposed, "the reservation must be released on cancellation.");
    }

    [Test]
    public async Task ExecuteAsync_StripsApprovalRequiredToolsFromTheUnattendedOffer()
    {
        using var harness = new Harness();
        var autoTool = new AllowedToolDto { Id = Guid.NewGuid(), Name = "get_current_time", Location = ToolLocation.ApiSide, RequiresApproval = false };
        var approvalTool = new AllowedToolDto { Id = Guid.NewGuid(), Name = "mcp_dangerous", Location = ToolLocation.ClientLocal, RequiresApproval = true };
        harness.Resolver
               .ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
               .Returns(new ResolvedAgentRuntime("SCAFFOLD+PERSONA", [autoTool, approvalTool], null, null, 7, AgentId, "Toolful Agent", []));

        await harness.Handler.ExecuteAsync(Context(ValidParams()), CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.CapturedPackage!.AllowedTools.Count);
        AssertEx.Equal("get_current_time", harness.CapturedPackage.AllowedTools[0].Name);
    }

    [Test]
    public async Task ExecuteAsync_OnSuccess_RecordsContentSafeSummaryWithoutPromptText()
    {
        using var harness = new Harness();
        string? reportedSummary = null;

        await harness.Handler.ExecuteAsync(Context(ValidParams(), (message, _, _) =>
        {
            reportedSummary = message;
            return Task.CompletedTask;
        }), CancellationToken.None);

        AssertEx.NotNull(reportedSummary);
        AssertEx.Contains(reportedSummary!, EffectiveLocalModel);
        AssertEx.False(reportedSummary!.Contains(Prompt, StringComparison.Ordinal), "the summary must never carry the prompt / message content.");
    }

    private static string ValidParams(string prompt = Prompt)
    {
        return $$"""{ "agentDefinitionId": "{{AgentIdString}}", "prompt": "{{prompt}}" }""";
    }

    private static ScheduledJobExecutionContext Context(string? parametersJson, Func<string, int?, CancellationToken, Task>? reportProgress = null)
    {
        return new ScheduledJobExecutionContext
        {
            ScheduledJobId = Guid.NewGuid(),
            TemplateId = RunSavedAgentHandler.TemplateIdValue,
            DisplayName = "Nightly log summary",
            Parameters = parametersJson,
            FireInstanceId = "fire-1",
            ScheduledFireTimeUtc = null,
            ActualFireTimeUtc = DateTimeOffset.UnixEpoch,
            TriggeredBy = ScheduledRunTrigger.Schedule,
            ReportProgressAsync = reportProgress
        };
    }

    private static AgentDefinitionRecord BuildDefinition(string? modelProfile)
    {
        return new AgentDefinitionRecord(AgentId,
            "Log Summarizer",
            Description: null,
            Instructions: "raw instructions (must NOT be used directly)",
            ModelProfile: modelProfile,
            ReasoningEffort: null,
            Kind: AgentDefinitionKind.Single,
            AllowedToolNames: [],
            ToolApprovals: new Dictionary<string, bool>(StringComparer.Ordinal),
            OrchestrationTopologyJson: null,
            Version: 7,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0);
    }

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            NodeSettings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
            LocalDefault.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(EffectiveLocalModel);
            Store.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(BuildDefinition(modelProfile: null));
            Capability.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns((SupportsThinking: true, SupportsTools: true, IsCloud: false));
            Capacity
                .DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                .Returns(new CapacityDecision(CapacityVerdict.Allow, "Capacity available.", OllamaEvictionWarning: false, _reservation));
            Resolver
                .ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("SCAFFOLD+PERSONA", [], null, "medium", 7, AgentId, "Log Summarizer", []));
            Dispatcher
                .ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>())
                .Returns(Substitute.For<IAsyncDisposable>());
            Runner
                .When(runner => runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>()))
                .Do(callInfo =>
                {
                    CapturedPackage = callInfo.Arg<InvocationExecutionContext>().Package;
                    RunCount++;
                });

            var services = new ServiceCollection();
            services.AddSingleton(Store);
            services.AddSingleton(Resolver);
            services.AddSingleton(Capability);
            services.AddSingleton(LocalDefault);
            services.AddSingleton(NodeSettings);
            services.AddSingleton(Capacity);
            services.AddSingleton<ILocalChatRuntimePackageBuilder, LocalChatRuntimePackageBuilder>();
            services.AddSingleton(Runner);
            services.AddSingleton(Dispatcher);
            var provider = services.BuildServiceProvider();

            Handler = new RunSavedAgentHandler(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RunSavedAgentHandler>.Instance);
        }

        private readonly TrackingDisposable _reservation = new();

        public IAgentDefinitionStore Store { get; } = Substitute.For<IAgentDefinitionStore>();

        public IAgentDefinitionResolver Resolver { get; } = Substitute.For<IAgentDefinitionResolver>();

        public IModelCapabilityResolver Capability { get; } = Substitute.For<IModelCapabilityResolver>();

        public ILocalDefaultChatModelResolver LocalDefault { get; } = Substitute.For<ILocalDefaultChatModelResolver>();

        public INodeSettingsStore NodeSettings { get; } = Substitute.For<INodeSettingsStore>();

        public ICapacityService Capacity { get; } = Substitute.For<ICapacityService>();

        public IInvocationRunner Runner { get; } = Substitute.For<IInvocationRunner>();

        public IWorkerEventDispatcher Dispatcher { get; } = Substitute.For<IWorkerEventDispatcher>();

        public RunSavedAgentHandler Handler { get; }

        public RuntimePackage? CapturedPackage { get; private set; }

        public int RunCount { get; private set; }

        public bool ReservationDisposed => _reservation.Disposed;

        public void Dispose()
        {
            _reservation.Dispose();
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
