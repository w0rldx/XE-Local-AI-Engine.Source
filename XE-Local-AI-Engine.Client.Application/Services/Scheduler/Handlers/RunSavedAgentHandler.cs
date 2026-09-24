namespace XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Models.Enums;
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
///     Quartz template handler for the <c>run-agent</c> template: it runs a saved node-local agent on a schedule with
///     a fixed prompt, through the SAME <see cref="IInvocationRunner" /> the local chat send path uses.
/// </summary>
/// <remarks>
///     <b>Node-local only, a security invariant:</b> the EFFECTIVE model, after the agent's pinned
///     <c>ModelProfile</c>, is classified through <see cref="IModelCapabilityResolver" /> and a cloud or remote one is
///     rejected UP FRONT, before capacity or any invocation, so unattended work never hands node-local content to a
///     cloud model. The handler is a singleton the registry captures at construction, scoping per fire, and owns no
///     scheduler state: no run rows, no notifications. See docs/wiki/06-scheduler.md ("Shipped templates").
/// </remarks>
public sealed class RunSavedAgentHandler : IScheduledJobHandler
{
    /// <summary>The reserved scheduler template id this handler claims.</summary>
    public const string TemplateIdValue = "run-agent";

    /// <summary>
    ///     JSON-Schema (draft-07) for the decrypted <c>run-agent</c> parameters: the saved agent to run, the fixed
    ///     prompt fed as the seed user turn, and an optional reasoning-effort override.
    /// </summary>
    /// <remarks>
    ///     The override wins over the agent's own effort, and every value is validated again in code before use.
    /// </remarks>
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

    public ScheduledJobTemplateDescriptor Descriptor { get; } = new()
    {
        TemplateId = TemplateIdValue,
        DisplayName = "Run a saved agent",
        Description = "Runs a saved agent on a schedule with a fixed prompt. Node-local models only.",
        ParameterSchema = ParameterSchemaJson,
        DefaultParameters = null,
        SupportedScheduleKinds = [ScheduleKind.Cron, ScheduleKind.OneShot, ScheduleKind.SimpleInterval, ScheduleKind.Manual],
        // Recurring execution is the point of this template, so Cron is the pre-selected kind; the other kinds stay
        // supported for a one-off or an operator-triggered "Run now".
        DefaultScheduleKind = ScheduleKind.Cron,
        DefaultMisfirePolicy = SchedulerMisfirePolicy.SkipMissed,
        // No template default: a value here becomes the form's pre-filled ceiling, and a fixed one caps every unattended run below a
        // raised node "Maximum message request timeout". Blank, the management service derives the ceiling from that node setting.
        DefaultMaxRuntimeSeconds = null,
        AllowManualTrigger = true,
        // This is the whole point of the run-agent template: the AI agent is permitted to schedule saved-agent runs.
        AllowAgentCreation = true,
        HistoryDetailLevel = HistoryDetailLevel.Detailed
    };

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
        var definition = await agentDefinitionStore.GetByIdAsync(parameters.AgentDefinitionId, cancellationToken);
        if (definition is null)
        {
            throw new ScheduledJobExecutionException("The scheduled agent could not be found. It may have been deleted.");
        }

        // 2. Resolve the EFFECTIVE model: the agent's pinned ModelProfile when set, else the node's local-default installed GGUF chat
        //    model. An unattended run has no user-picked model, and a null effective model fails clearly rather than reaching a dead provider.
        var nodeSettings = await nodeSettingsStore.LoadAsync(cancellationToken);
        var localDefaultModel = await localDefaultResolver.ResolveAsync(nodeSettings.DefaultModelName, cancellationToken);
        var pinnedModel = string.IsNullOrWhiteSpace(definition.ModelProfile) ? null : definition.ModelProfile;
        var effectiveModel = pinnedModel ?? localDefaultModel;
        if (string.IsNullOrWhiteSpace(effectiveModel))
        {
            throw new ScheduledJobExecutionException("No local chat model is available to run the scheduled agent. Install a local model or pin one to the agent.");
        }

        // 3. LOCALITY GATE (security invariant): classify the effective model and reject a cloud or remote one UP FRONT, before
        //    capacity or any invocation, so unattended work stays node-local. Same classification the chat locality gate uses.
        var capabilities = await modelCapabilityResolver.ResolveAsync(effectiveModel, cancellationToken);
        var (supportsThinking, supportsTools, effectiveModelIsCloud) = capabilities;
        if (effectiveModelIsCloud)
        {
            _logger.LogInformation("Scheduled agent run for definition {AgentDefinitionId} was rejected: its effective model is cloud-hosted and unattended runs are node-local only.",
                definition.Id);
            throw new ScheduledJobExecutionException("Scheduled agent runs are restricted to node-local models. This agent is configured to use a cloud model, so it will not run unattended.");
        }

        // 4. Resolve the agent's COMPLETE runtime and build the headless package. Passing the effective model as the active model
        //    keeps the resolver's model identical to the gated one, and the resolved prompt is threaded verbatim, never raw Instructions.
        var resolved = await agentDefinitionResolver.ResolveAsync(definition.Id,
            effectiveModel,
            retrievalQuery: parameters.Prompt,
            supportsTools,
            honorModelProfile: true,
            effectiveModelIsCloud,
            cancellationToken);
        if (resolved is null)
        {
            // The definition existed at step 1 but was deleted before the resolve completed (rare race).
            throw new ScheduledJobExecutionException("The scheduled agent could not be found. It may have been deleted.");
        }

        var package = BuildPackage(packageBuilder,
            resolved,
            effectiveModel,
            parameters,
            supportsThinking,
            capabilities.ReasoningBudgetEnforceable,
            nodeSettings.MaxMessageRequestTimeoutSeconds);

        // 5. Take the shared invocation slot, THEN decide capacity, then run headless. Deciding before the slot let a second fire for the same
        //    cold model see the first fire's footprint reservation and be refused instead of queueing (F-32); the integration path uses this order.
        await RunAndSummarizeAsync(eventDispatcher, capacityService, invocationRunner, package, context, effectiveModel, cancellationToken);
    }

    /// <summary>
    ///     Builds the headless loopback runtime package from the agent's resolved runtime, with the prompt as the
    ///     single seed user turn.
    /// </summary>
    /// <remarks>
    ///     Approval-required tools are stripped from the offer: an unattended run has no human-in-the-loop round-trip,
    ///     so an approval-gated tool — an MCP tool ships approval-required by default — would raise a request nobody
    ///     can answer and hang the run until its max-runtime interrupt, the same rationale by which a spawned sub-agent
    ///     drops them. The effective model is bound as a concrete <c>ModelProfile</c>, so the runner never falls back
    ///     to the node default, and the whole-turn deadline is the node "Maximum message request timeout".
    /// </remarks>
    private RuntimePackage BuildPackage(ILocalChatRuntimePackageBuilder packageBuilder,
        ResolvedAgentRuntime resolved,
        string effectiveModel,
        RunSavedAgentParameters parameters,
        bool supportsThinking,
        bool reasoningBudgetEnforceable,
        int maxMessageRequestTimeoutSeconds)
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

        // A per-run reasoning-effort override wins over the agent's own effort ONLY when it is a recognized effort: Normalize returns
        // null for a blank or unrecognized one, so both fall back here, where the builder's own normalize would suppress reasoning.
        var overrideEffort = ReasoningEffortNormalizer.Normalize(parameters.ReasoningEffort);
        var reasoningEffort = overrideEffort ?? resolved.ReasoningEffort;

        var seedTurn = new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = parameters.Prompt,
            SortOrder = 0
        };

        return packageBuilder.Build(new LocalChatRuntimePackageRequest
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            ResolvedSystemPrompt = resolved.ResolvedSystemPrompt,
            ConversationContext = [seedTurn],
            ModelProfile = effectiveModel,
            AgentDefinitionVersion = resolved.AgentDefinitionVersion,
            ClientNodeId = LocalChatLoopbackDefaults.ClientNodeId,
            AllowedTools = offeredTools,
            // Only the invocation timeout is operator-controlled, tool-call and stream-idle keeping their defaults. At the default
            // setting the package, and its config hash, stay byte-identical to one built without explicit timeouts.
            Timeouts = new TimeoutSettings
            {
                InvocationTimeoutSeconds = maxMessageRequestTimeoutSeconds
            },
            ReasoningEffort = reasoningEffort,
            SupportsThinking = supportsThinking,
            ReasoningBudgetEnforceable = reasoningBudgetEnforceable,
            Skills = resolved.Skills,
            // The one place this flag is set: stripping approval-required tools from the OFFER cannot cover skill tools, which arrive
            // through MAF's AIContextProviders, so the flag fails their approval request at once instead of parking the run for MaxPendingToolCallAge.
            IsUnattended = true
        });
    }

    /// <summary>
    ///     Runs the headless package through the shared <see cref="IInvocationRunner" />, holding the node-wide
    ///     invocation slot so a scheduled run serializes against in-flight chat turns, with no concurrent model loads.
    /// </summary>
    /// <remarks>
    ///     The slot registration also makes the runner's terminal report fire
    ///     <see cref="IWorkerEventDispatcher.InvocationStateChanged" />, captured here for a content-safe summary. The
    ///     runner SWALLOWS <see cref="OperationCanceledException" />, reporting Cancelled and returning, so
    ///     cancellation is re-surfaced explicitly after the run; a terminal failure throws an operator-safe
    ///     <see cref="ScheduledJobExecutionException" /> WITHOUT leaking the raw runner error.
    /// </remarks>
    private static async Task RunAndSummarizeAsync(IWorkerEventDispatcher eventDispatcher,
        ICapacityService capacityService,
        IInvocationRunner invocationRunner,
        RuntimePackage package,
        ScheduledJobExecutionContext context,
        string effectiveModel,
        CancellationToken cancellationToken)
    {
        // Captured through a reference holder rather than a plain local: the terminal is assigned only inside the event handler below,
        // which flow analysis cannot see fires synchronously, so a plain local would be wrongly proven always-null.
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
        var lease = await eventDispatcher.ReportInvocationAssignedAsync(package, cancellationToken);
        IDisposable? reservation = null;
        eventDispatcher.InvocationStateChanged += OnInvocationStateChanged;
        try
        {
            // RejectInsufficient fails with the sanitized reason; a local Allow carries a footprint reservation that MUST be disposed, or later
            // spawns are wrongly rejected; QueueSameModel reuses a resident model and carries none.
            var decision = await capacityService.DecideAsync(effectiveModel, ModelRole.Chat, cancellationToken);
            if (decision.Verdict == CapacityVerdict.RejectInsufficient)
            {
                throw new ScheduledJobExecutionException(decision.Reason);
            }

            reservation = decision.Reservation;
            var executionContext = InvocationExecutionContext.CreatePlain(package, Guid.Empty);
            await invocationRunner.RunAsync(executionContext, cancellationToken);
        }
        catch (Exception exception) when (terminalState.Value is null)
        {
            // The slot already published Assigned; a refusal, fault or cancel before the runner reports would leave that phantom
            // (drafting reads it as busy, the monitor shows a stuck run), so the terminal is reported here with a sanitized message.
            await eventDispatcher.ReportInvocationFailedAsync(package.InvocationId,
                exception is ScheduledJobExecutionException sanitized ? sanitized.Message : "The scheduled agent run could not start.",
                exception is OperationCanceledException ? FailureCategory.Cancelled : FailureCategory.ModelUnavailable);
            throw;
        }
        finally
        {
            eventDispatcher.InvocationStateChanged -= OnInvocationStateChanged;
            // Reverse acquisition order: the footprint is released before the slot, so the next queued fire decides against the settled ledger.
            reservation?.Dispose();
            await lease.DisposeAsync();
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
                // Cancelled without our own token firing, as an operator force-eject of the model mid-run does. Record a sanitized
                // failure rather than a token-less OperationCanceledException, which the dispatcher would mis-record as TimedOut.
                throw new ScheduledJobExecutionException("The scheduled agent run was interrupted before it completed.");
            default:
                await ReportRunSummaryAsync(context, effectiveModel, terminalState.Value, cancellationToken);
                break;
        }
    }

    /// <summary>
    ///     Records a CONTENT-SAFE run summary — effective model, token totals, generation duration, never message
    ///     content or prompt text — through the dispatcher-supplied progress callback.
    /// </summary>
    /// <remarks>
    ///     The callback appends a run-history event and may be null on Summary-level dispatch. A null terminal state,
    ///     from a run whose completion never reached the slot, still records a bare model-only summary.
    /// </remarks>
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
        return new RunSavedAgentParameters
        {
            AgentDefinitionId = agentDefinitionId,
            Prompt = dto.Prompt.Trim(),
            ReasoningEffort = reasoningEffort
        };
    }

    /// <summary>Validated, code-facing parameters for one <c>run-agent</c> fire.</summary>
    private sealed record RunSavedAgentParameters
    {
        public required Guid AgentDefinitionId { get; init; }

        public required string Prompt { get; init; }

        public required string? ReasoningEffort { get; init; }
    }

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
