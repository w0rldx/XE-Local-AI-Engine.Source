namespace XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Quartz template handler for the <c>run-agent</c> template: runs a saved node-local agent on a schedule
///     with a fixed prompt. On each fire it loads the bound agent definition, resolves its COMPLETE
///     runtime (scaffold + persona + folded playbook memory, curated tools, skills, reasoning, version — never
///     the raw <c>Instructions</c>), builds a headless loopback <see cref="RuntimePackage" /> with the prompt as the seed
///     user turn, and drives it through the SAME <see cref="IInvocationRunner" /> the local chat send path uses — minus
///     the chat conversation/persistence pump. No new runtime-package builder is introduced; the assembly reuses
///     <see cref="IAgentDefinitionResolver" /> + <see cref="ILocalChatRuntimePackageBuilder" /> +
///     <see cref="InvocationExecutionContext.CreatePlain" /> verbatim.
///     <para>
///         <b>Singleton.</b> The registry captures every handler in a <c>FrozenDictionary</c> at construction, so this
///         handler is effectively a singleton and CANNOT inject scoped services. It injects
///         <see cref="IServiceScopeFactory" /> and creates a scope per <see cref="ExecuteAsync" />, resolving the scoped
///         collaborators inside (mirrors <see cref="ModelRecommendationCheckHandler" />).
///     </para>
///     <para>
///         <b>Node-local only (security invariant).</b> Unattended scheduled work never egresses to a cloud provider:
///         the EFFECTIVE model (after the agent's pinned <c>ModelProfile</c>) is classified through the shared
///         <see cref="IModelCapabilityResolver" /> and a cloud/remote effective model is rejected UP FRONT — before the
///         capacity gate or any invocation — so node-local prompt/agent content is never handed to a cloud model on an
///         unattended run.
///     </para>
///     <para>
///         <b>Owns no scheduler state.</b> It never writes scheduler run rows and never publishes SignalR — the dispatcher
///         owns those (durability + restart reconciliation are inherited). It records a CONTENT-SAFE run summary
///         (status/model/tokens/duration — never message content) through
///         <see cref="ScheduledJobExecutionContext.ReportProgressAsync" />, lets <see cref="OperationCanceledException" />
///         propagate (dispatcher records Cancelled/TimedOut), and throws a <see cref="ScheduledJobExecutionException" />
///         with an operator-safe reason on any failure.
///     </para>
/// </summary>
public sealed class RunSavedAgentHandler : IScheduledJobHandler
{
    /// <summary>The reserved scheduler template id this handler claims.</summary>
    public const string TemplateIdValue = "run-agent";

    /// <summary>
    ///     JSON-Schema (draft-07) for the decrypted <c>run-agent</c> parameters: the saved agent to run
    ///     (<c>agentDefinitionId</c>), the fixed prompt fed as the seed user turn (<c>prompt</c>), and an optional
    ///     reasoning-effort override that wins over the agent's own effort. Values are validated again in code before use.
    /// </summary>
    private const string ParameterSchemaJson =
        """
        {
          "$schema": "http://json-schema.org/draft-07/schema#",
          "type": "object",
          "additionalProperties": false,
          "required": ["agentDefinitionId", "prompt"],
          "properties": {
            "agentDefinitionId": { "type": "string", "format": "uuid", "minLength": 1 },
            "prompt": { "type": "string", "minLength": 1 },
            "reasoningEffort": { "type": ["string", "null"] }
          }
        }
        """;

    private static readonly JsonSerializerOptions ParameterSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<RunSavedAgentHandler> _logger;

    private readonly IServiceScopeFactory _scopeFactory;

    public RunSavedAgentHandler(IServiceScopeFactory scopeFactory, ILogger<RunSavedAgentHandler> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string TemplateId => TemplateIdValue;

    public ScheduledJobTemplateDescriptor Descriptor { get; } = new(TemplateIdValue,
        "Run a saved agent",
        "Runs a saved agent on a schedule with a fixed prompt. Node-local models only.",
        ParameterSchemaJson,
        DefaultParameters: null,
        [ScheduleKind.Cron, ScheduleKind.OneShot, ScheduleKind.SimpleInterval, ScheduleKind.Manual],
        // Recurring execution is the point of this template, so Cron is the pre-selected kind; the other kinds stay
        // supported for a one-off or an operator-triggered "Run now".
        ScheduleKind.Cron,
        SchedulerMisfirePolicy.SkipMissed,
        DefaultMaxRuntimeSeconds: 600,
        AllowManualTrigger: true,
        // This is the whole point of the run-agent template: the AI agent is permitted to schedule saved-agent runs.
        AllowAgentCreation: true,
        HistoryDetailLevel.Detailed);

    public async Task ExecuteAsync(ScheduledJobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var parameters = ParseAndValidate(context.Parameters);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var agentDefinitionStore = services.GetRequiredService<IAgentDefinitionStore>();
        var agentDefinitionResolver = services.GetRequiredService<IAgentDefinitionResolver>();
        var modelCapabilityResolver = services.GetRequiredService<IModelCapabilityResolver>();
        var localDefaultResolver = services.GetRequiredService<ILocalDefaultChatModelResolver>();
        var nodeSettingsStore = services.GetRequiredService<INodeSettingsStore>();
        var capacityService = services.GetRequiredService<ICapacityService>();
        var packageBuilder = services.GetRequiredService<ILocalChatRuntimePackageBuilder>();
        var invocationRunner = services.GetRequiredService<IInvocationRunner>();
        var eventDispatcher = services.GetRequiredService<IWorkerEventDispatcher>();

        // 1. Load the saved agent. A missing (or since-deleted) definition fails with a sanitized reason — there is no
        //    "disabled" flag on an AgentDefinition, so "missing" is the only unavailable state.
        var definition = await agentDefinitionStore.GetByIdAsync(parameters.AgentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            throw new ScheduledJobExecutionException("The scheduled agent could not be found. It may have been deleted.");
        }

        // 2. Resolve the EFFECTIVE model: the agent's pinned ModelProfile when set, otherwise the node's local-default
        //    installed GGUF chat model (never Ollama, never cloud). An unattended run has no user-picked model, so the
        //    pin — or the local default — is the authoritative model the turn binds via ChatOptions.ModelId. A null
        //    effective model (no pin AND no installed local chat model) fails clearly rather than silently falling back
        //    to a dead provider.
        var nodeSettings = await nodeSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var localDefaultModel = await localDefaultResolver.ResolveAsync(nodeSettings.DefaultModelName, cancellationToken).ConfigureAwait(false);
        var pinnedModel = string.IsNullOrWhiteSpace(definition.ModelProfile) ? null : definition.ModelProfile;
        var effectiveModel = pinnedModel ?? localDefaultModel;
        if (string.IsNullOrWhiteSpace(effectiveModel))
        {
            throw new ScheduledJobExecutionException("No local chat model is available to run the scheduled agent. Install a local model or pin one to the agent.");
        }

        // 3. LOCALITY GATE (security invariant): classify the effective model and reject a cloud/remote model UP FRONT —
        //    before capacity or any invocation — so unattended scheduled work stays node-local-only. This is the SAME
        //    effective-model classification the chat locality gate uses (IModelCapabilityResolver).
        var (supportsThinking, supportsTools, effectiveModelIsCloud) =
            await modelCapabilityResolver.ResolveAsync(effectiveModel, cancellationToken).ConfigureAwait(false);
        if (effectiveModelIsCloud)
        {
            _logger.LogInformation("Scheduled agent run for definition {AgentDefinitionId} was rejected: its effective model is cloud-hosted and unattended runs are node-local only.",
                definition.Id);
            throw new ScheduledJobExecutionException("Scheduled agent runs are restricted to node-local models. This agent is configured to use a cloud model, so it will not run unattended.");
        }

        // 4. CAPACITY / GPU admission for the effective model. A RejectInsufficient verdict fails with the sanitized
        //    reason constant; a local Allow carries a footprint reservation that MUST be disposed on completion (a leaked
        //    reservation wrongly rejects later spawns); QueueSameModel means the model is already resident, so the run
        //    reuses it with no second load (null reservation). GPU load serialization is inherited from the supervisor.
        var decision = await capacityService.DecideAsync(effectiveModel, ModelRole.Chat, cancellationToken).ConfigureAwait(false);
        if (decision.Verdict == CapacityVerdict.RejectInsufficient)
        {
            throw new ScheduledJobExecutionException(decision.Reason);
        }

        var reservation = decision.Reservation;
        try
        {
            // 5. Resolve the agent's COMPLETE runtime and build the headless package. Passing the effective model as the
            //    active model with honorModelProfile:true keeps the resolver's effective model identical to the one gated
            //    above (pin ?? effectiveModel). The resolved prompt (scaffold + persona + folded playbook memory), curated
            //    tools, skills, reasoning, and version are threaded verbatim — NOT the raw definition.Instructions.
            var resolved = await agentDefinitionResolver.ResolveAsync(definition.Id,
                                                            effectiveModel,
                                                            retrievalQuery: parameters.Prompt,
                                                            supportsTools,
                                                            honorModelProfile: true,
                                                            effectiveModelIsCloud,
                                                            cancellationToken)
                                                        .ConfigureAwait(false);
            if (resolved is null)
            {
                // The definition existed at step 1 but was deleted before the resolve completed (rare race).
                throw new ScheduledJobExecutionException("The scheduled agent could not be found. It may have been deleted.");
            }

            var package = BuildPackage(packageBuilder, resolved, effectiveModel, parameters, supportsThinking);

            // 6. Run headless through the shared invocation runner, serialized against in-flight chat/platform turns via
            //    the shared invocation slot, capturing a content-safe terminal summary.
            await RunAndSummarizeAsync(eventDispatcher, invocationRunner, package, context, effectiveModel, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Release the reserved footprint on every terminal path (success, failure, or cancellation). Disposing a null
            // reservation (cloud/QueueSameModel) is a no-op; disposing a real one is idempotent.
            reservation?.Dispose();
        }
    }

    /// <summary>
    ///     Builds the headless loopback runtime package from the agent's resolved runtime with the prompt as the single
    ///     seed user turn. Approval-required tools are stripped from the offer: an unattended scheduled run has no
    ///     human-in-the-loop approval round-trip, so an approval-gated tool (e.g. an MCP tool, which ships
    ///     approval-required by default) would surface a tool-approval request nobody can answer and hang the run until
    ///     its max-runtime interrupt — the same no-HITL rationale by which a spawned sub-agent drops approval-required
    ///     tools. The effective model is bound as a concrete <c>ModelProfile</c> so the runner never
    ///     silently falls back to the node default.
    /// </summary>
    private RuntimePackage BuildPackage(ILocalChatRuntimePackageBuilder packageBuilder,
        ResolvedAgentRuntime resolved,
        string effectiveModel,
        RunSavedAgentParameters parameters,
        bool supportsThinking)
    {
        var offeredTools = resolved.AllowedTools.Where(static tool => !tool.RequiresApproval).ToArray();

        var strippedTools = resolved.AllowedTools.Where(static tool => tool.RequiresApproval).ToArray();
        if (strippedTools.Length > 0)
        {
            _logger.LogWarning(
                "Run-agent for definition {AgentDefinitionId} stripped {StrippedCount} approval-required tool(s) ({StrippedTools}) from the unattended offer: a scheduled run has no approval round-trip.",
                resolved.AgentDefinitionId,
                strippedTools.Length,
                string.Join(", ", strippedTools.Select(static tool => tool.Name)));
        }

        // A per-run reasoning-effort override wins over the agent's own effort ONLY when it is a recognized effort.
        // Normalize returns null for a blank OR unrecognized override, so both fall back to the agent's resolved
        // effort here — an invalid override (e.g. "banana") must never reach the builder, whose own normalize step
        // would silently drop it to null and suppress reasoning instead of honoring the agent's own effort.
        var overrideEffort = ReasoningEffortNormalizer.Normalize(parameters.ReasoningEffort);
        var reasoningEffort = overrideEffort ?? resolved.ReasoningEffort;

        var seedTurn = new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = parameters.Prompt,
            SortOrder = 0
        };

        return packageBuilder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            resolved.ResolvedSystemPrompt,
            [seedTurn],
            effectiveModel,
            resolved.AgentDefinitionVersion,
            LocalChatLoopbackDefaults.ClientNodeId,
            offeredTools,
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
            ReasoningEffort: reasoningEffort,
            SupportsThinking: supportsThinking,
            Skills: resolved.Skills,
            // The one place this flag is set. Stripping approval-required tools from the OFFER above cannot cover the
            // skill tools: they arrive through MAF's AIContextProviders (progressive disclosure), never through the
            // offer, so an assigned skill still surfaces an approval request here. The flag lets the runner fail that
            // request immediately with an explicit reason instead of parking the scheduled run on the full
            // MaxPendingToolCallAge window first.
            IsUnattended: true));
    }

    /// <summary>
    ///     Runs the headless package through the shared <see cref="IInvocationRunner" />, holding the node-wide invocation
    ///     slot for the duration so a scheduled run serializes against in-flight chat/platform turns (no concurrent model
    ///     loads). The slot registration also makes the runner's terminal report fire
    ///     <see cref="IWorkerEventDispatcher.InvocationStateChanged" />, which is captured here to record a content-safe
    ///     summary. The runner SWALLOWS <see cref="OperationCanceledException" /> (it reports Cancelled to the dispatcher
    ///     and returns), so cancellation is re-surfaced explicitly after the run; a terminal failure throws an
    ///     operator-safe <see cref="ScheduledJobExecutionException" /> WITHOUT leaking the raw runner error.
    /// </summary>
    private static async Task RunAndSummarizeAsync(IWorkerEventDispatcher eventDispatcher,
        IInvocationRunner invocationRunner,
        RuntimePackage package,
        ScheduledJobExecutionContext context,
        string effectiveModel,
        CancellationToken cancellationToken)
    {
        // Captured through a reference holder rather than a plain local: the terminal is assigned only inside the
        // event handler below, which flow analysis cannot see fires synchronously from RunAsync's completion report, so a
        // plain local would be (wrongly) proven always-null. The holder's field write breaks that false inference.
        var terminalState = new StrongBox<InvocationState?>(null);

        void OnInvocationStateChanged(object? sender, InvocationStateChangedEventArgs args)
        {
            if (args.State.InvocationId == package.InvocationId
                && args.State.Status is InvocationStatus.Completed or InvocationStatus.Failed or InvocationStatus.Cancelled)
            {
                terminalState.Value = args.State;
            }
        }

        // Acquire the shared invocation slot before running. Cancelling while still queued behind another invocation
        // aborts the wait here (OperationCanceledException propagates to the dispatcher as Cancelled/TimedOut).
        var lease = await eventDispatcher.ReportInvocationAssignedAsync(package, cancellationToken).ConfigureAwait(false);
        eventDispatcher.InvocationStateChanged += OnInvocationStateChanged;
        try
        {
            using var executionContext = InvocationExecutionContext.CreatePlain(package, Guid.Empty);
            await invocationRunner.RunAsync(executionContext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            eventDispatcher.InvocationStateChanged -= OnInvocationStateChanged;
            await lease.DisposeAsync().ConfigureAwait(false);
        }

        // The runner reports Cancelled to the dispatcher and returns normally on cancellation rather than rethrowing, so
        // re-surface it here: the dispatcher then records the run as Cancelled (operator) or TimedOut (auto-interrupt).
        cancellationToken.ThrowIfCancellationRequested();

        switch (terminalState.Value?.Status)
        {
            case InvocationStatus.Failed:
                // The raw terminal error may carry provider text; never surface it. The full detail is in the node logs.
                throw new ScheduledJobExecutionException("The scheduled agent run failed. See the node logs for details.");
            case InvocationStatus.Cancelled:
                // Cancelled without our own token firing (e.g. an operator force-eject of the model mid-run). Record a
                // sanitized failure rather than throwing a token-less OperationCanceledException (which the dispatcher
                // would mis-record as a max-runtime TimedOut).
                throw new ScheduledJobExecutionException("The scheduled agent run was interrupted before it completed.");
            default:
                await ReportRunSummaryAsync(context, effectiveModel, terminalState.Value, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    ///     Records a CONTENT-SAFE run summary (effective model + token totals + generation duration — never any message
    ///     content or prompt text) through the dispatcher-supplied progress callback, which appends a run-history event.
    ///     The callback may be null (Summary-level dispatch); a null terminal state (e.g. a run whose completion never
    ///     reached the slot) still records a bare model-only summary.
    /// </summary>
    private static Task ReportRunSummaryAsync(ScheduledJobExecutionContext context,
        string effectiveModel,
        InvocationState? terminalState,
        CancellationToken cancellationToken)
    {
        var reportProgress = context.ReportProgressAsync;
        if (reportProgress is null)
        {
            return Task.CompletedTask;
        }

        var tokenSuffix = terminalState?.TotalTokens is { } totalTokens
            ? $"; {totalTokens} tokens"
            : string.Empty;
        var durationSuffix = terminalState?.GenerationDurationMs is { } durationMs
            ? $"; {durationMs} ms"
            : string.Empty;
        var summary = $"Agent run completed on model '{effectiveModel}'{tokenSuffix}{durationSuffix}.";

        return reportProgress(summary, 100, cancellationToken);
    }

    /// <summary>
    ///     Parses and validates the decrypted parameter JSON. A blank/invalid <c>agentDefinitionId</c> or a blank
    ///     <c>prompt</c> throws <see cref="ScheduledJobValidationException" /> (the dispatcher records the failure without
    ///     invoking the runner). Never echoes raw parameter values.
    /// </summary>
    private static RunSavedAgentParameters ParseAndValidate(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            throw new ScheduledJobValidationException("Run-agent parameters are required.");
        }

        RunSavedAgentParametersDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<RunSavedAgentParametersDto>(parametersJson, ParameterSerializerOptions);
        }
        catch (JsonException)
        {
            throw new ScheduledJobValidationException("Run-agent parameters are not valid JSON.");
        }

        if (dto is null)
        {
            throw new ScheduledJobValidationException("Run-agent parameters are required.");
        }

        if (string.IsNullOrWhiteSpace(dto.AgentDefinitionId) || !Guid.TryParse(dto.AgentDefinitionId, out var agentDefinitionId) || agentDefinitionId == Guid.Empty)
        {
            throw new ScheduledJobValidationException("A valid agent must be selected for the scheduled run.");
        }

        if (string.IsNullOrWhiteSpace(dto.Prompt))
        {
            throw new ScheduledJobValidationException("A prompt is required for the scheduled agent run.");
        }

        var reasoningEffort = string.IsNullOrWhiteSpace(dto.ReasoningEffort) ? null : dto.ReasoningEffort.Trim();
        return new RunSavedAgentParameters(agentDefinitionId, dto.Prompt.Trim(), reasoningEffort);
    }

    /// <summary>Validated, code-facing parameters for one <c>run-agent</c> fire.</summary>
    private sealed record RunSavedAgentParameters(Guid AgentDefinitionId, string Prompt, string? ReasoningEffort);

    /// <summary>Decrypted-parameter wire shape for the <c>run-agent</c> template.</summary>
    private sealed record RunSavedAgentParametersDto
    {
        [JsonPropertyName("agentDefinitionId")]
        public string? AgentDefinitionId { get; init; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; init; }

        [JsonPropertyName("reasoningEffort")]
        public string? ReasoningEffort { get; init; }
    }
}
