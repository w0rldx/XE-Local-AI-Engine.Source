namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Assembles a settling node run's cost from persisted rows. Three shapes, decided by the node run itself: a work
///     session takes the agent path, a development task takes the DevTask path, and a node run with neither — Tool,
///     Gate, Parallel, Join, and every <c>Skipped</c> row — has no cost rows to read and answers nothing.
/// </summary>
/// <remarks>
///     <paramref name="development" /> is optional BECAUSE the whole Development module is: every one of its services,
///     <c>IDevelopmentStore</c> included, is registered only when <c>Development:Enabled</c> is true
///     (<c>AddNodeDevelopmentExtensions</c>), while this collector is registered unconditionally beside the workflow
///     store that every node-run transition goes through. A required dependency would therefore fail to resolve
///     <c>IDevWorkflowStore</c> itself on a node with Development Mode switched off. The container fills a defaulted
///     parameter with null when the service is absent, which is the same answer the DevTask lane gives for the same
///     reason — and the node run simply reports no DevTask cost.
///     <para>
///         <paramref name="localModelLoads" /> is optional for the same reason: it is registered by the model-fit
///         module, which a host composing only the workflow stack does not add. Absent, the VRAM columns stay null,
///         which is what they say anyway for every model the node did not load itself.
///     </para>
/// </remarks>
internal sealed class DevWorkflowNodeTelemetrySource(IAgentWorkSessionStore workSessions,
    IAgentExecutionLogStore executionLogs,
    IDevelopmentStore? development = null,
    NodeMetricsLlamaServerLoadTelemetry? localModelLoads = null) : IDevWorkflowNodeTelemetrySource
{
    /// <summary>camelCase, matching the supervisor that wrote these rows.</summary>
    private static readonly JsonSerializerOptions ConsumptionJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The step-event types that carry a consumption record. Scoped rather than "every event with a detail",
    ///     because <c>ListEventsAsync</c> is unbounded and returns every type the session ever wrote.
    /// </summary>
    private static readonly HashSet<string> ConsumptionEventTypes =
    [
        WorkSessionEventTypes.StepEnded,
        WorkSessionEventTypes.StepFailed
    ];

    /// <summary>One envelope page. The read is a filtered scan — there is no <c>conversation_id</c> index — so it pages.</summary>
    private const int EnvelopePageSize = 200;

    /// <summary>
    ///     How many pages the envelope read walks before it stops. A conversation past this is pathological; under-counting
    ///     it is a lower bound the runbook already states, whereas scanning it without end would be a dispatcher tick that
    ///     never returns.
    /// </summary>
    private const int MaxEnvelopePages = 20;

    /// <summary>The schema's own bound on the node run's <c>tool_names_json</c> (<c>DevWorkflowNodeRunConfiguration</c>).</summary>
    private const int MaxToolNamesJson = 1024;

    /// <summary>
    ///     The schema's bound on <c>served_model_name</c>. Clamped HERE, by construction, like both sibling text
    ///     columns: <c>agent_execution_logs.model_name</c> declares no length of its own and SQLite enforces none, so a
    ///     name copied verbatim would make the column's declared bound a lie.
    /// </summary>
    private const int MaxServedModelName = 256;

    /// <summary>The final element of a trimmed name list. One character, and unmistakably not a tool.</summary>
    private const string TruncatedToolNameMarker = "…";

    private readonly IAgentWorkSessionStore _workSessions = workSessions ?? throw new ArgumentNullException(nameof(workSessions));
    private readonly IAgentExecutionLogStore _executionLogs = executionLogs ?? throw new ArgumentNullException(nameof(executionLogs));
    private readonly IDevelopmentStore? _development = development;
    private readonly NodeMetricsLlamaServerLoadTelemetry? _localModelLoads = localModelLoads;

    public async Task<DevWorkflowNodeTelemetry?> CollectAsync(DevWorkflowNodeRunSnapshot nodeRun,
        DevWorkflowNodeRunStatus targetStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeRun);

        // The same gate the decorator applies, restated here so a future caller cannot collect a moving target: while a
        // node run is Queued or Running its session is still spending, and a number read then is a race.
        if (!DevWorkflowStateMachine.IsTerminal(targetStatus)
            && targetStatus is not (DevWorkflowNodeRunStatus.Blocked or DevWorkflowNodeRunStatus.WaitingForApproval))
        {
            return null;
        }

        if (nodeRun.WorkSessionId is { } sessionId && nodeRun.WorkSessionAvailable)
        {
            return await CollectFromWorkSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

        return nodeRun.DevelopmentTaskId is { } developmentTaskId
            ? await CollectFromDevelopmentTaskAsync(developmentTaskId, nodeRun.StartedAtUtc, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    ///     The agent path: the session's own step rows for what the loop spent, and the conversation's chat-run
    ///     envelopes for what the provider actually reported. The two are different questions and neither substitutes
    ///     for the other — the step rows exist even when no envelope was ever written, and only the envelopes carry
    ///     real provider tokens.
    /// </summary>
    private async Task<DevWorkflowNodeTelemetry> CollectFromWorkSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await _workSessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var steps = await CollectStepConsumptionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var envelopes = await CollectEnvelopesAsync(session.ConversationId, cancellationToken).ConfigureAwait(false);

        // As of the most recent SUCCESSFUL load of the model that served this run, which is not necessarily a load this
        // run caused: a warm run reports the earlier load's figures, and model_readiness_ms is what tells the two apart
        // (null there ⇒ no turn of this attempt warmed a runtime ⇒ the load predates the run). Chat role, because that
        // is the only role a work session's turns ever ask for. Null for a remote or Ollama model, and null when the
        // node never loaded this model itself.
        var vram = envelopes.ServedModelName is { } servedModel
            ? _localModelLoads?.TryGetLastReadyLoad(servedModel, ModelRole.Chat)
            : null;

        return new DevWorkflowNodeTelemetry(envelopes.InputTokens,
            envelopes.OutputTokens,
            envelopes.ReasoningTokens,
            steps.EstimatedInputTokens,
            steps.ProviderCalls,
            steps.ToolCalls,
            steps.ToolSchemaTokens,
            ToolNamesJson(steps.ToolNames),
            envelopes.AgentTurnMs,
            envelopes.ServedModelName,
            RouteJson: null,
            session.StepCount,
            envelopes.ModelReadinessMs,
            vram?.GlobalFreeVramBytesAtLoad,
            vram?.AdmittedVramBytes);
    }

    /// <summary>
    ///     The DevTask path: the task's attempts, coder and reviewer alike, successful and failed alike — all of them
    ///     are this node's cost.
    ///     <para>
    ///         Windowed on the node run's own <c>StartedAtUtc</c>, and that is required rather than tidy: a re-attempt
    ///         keeps the same development task, so without the window a node's third attempt would re-count the first
    ///         two attempts' tokens. A missing timestamp on either side answers nothing rather than guessing.
    ///     </para>
    /// </summary>
    private async Task<DevWorkflowNodeTelemetry?> CollectFromDevelopmentTaskAsync(Guid developmentTaskId,
        long? nodeRunStartedAtUtc,
        CancellationToken cancellationToken)
    {
        if (_development is null || nodeRunStartedAtUtc is not { } startedAtUtc)
        {
            return null;
        }

        var attempts = await _development.ListAttemptsAsync(developmentTaskId, cancellationToken).ConfigureAwait(false);

        long? inputTokens = null;
        long? outputTokens = null;
        foreach (var attempt in attempts.Where(attempt => attempt.StartedAtUtc >= startedAtUtc))
        {
            inputTokens = Add(inputTokens, attempt.InputTokens);
            outputTokens = Add(outputTokens, attempt.OutputTokens);
        }

        return new DevWorkflowNodeTelemetry(inputTokens, outputTokens);
    }

    /// <summary>
    ///     What the session's steps spent, off the <c>StepEnded</c> / <c>StepFailed</c> rows the supervisor wrote. A row
    ///     whose detail will not parse is skipped rather than thrown on: a settle must not fail over a measurement.
    /// </summary>
    private async Task<StepTotals> CollectStepConsumptionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var events = await _workSessions.ListEventsAsync(sessionId, sinceSequence: 0, cancellationToken).ConfigureAwait(false);

        int? providerCalls = null;
        long? estimatedInputTokens = null;
        int? toolCalls = null;
        long? toolSchemaTokens = null;
        var toolNames = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var row in events.Where(row => ConsumptionEventTypes.Contains(row.EventType) && !string.IsNullOrWhiteSpace(row.DetailJson)))
        {
            if (ReadConsumption(row.DetailJson!) is not { } consumption)
            {
                continue;
            }

            providerCalls = Add(providerCalls, consumption.ProviderCalls);
            estimatedInputTokens = Add(estimatedInputTokens, consumption.EstimatedInputTokens);
            toolCalls = Add(toolCalls, consumption.ToolCallsCompleted);
            toolSchemaTokens = Add(toolSchemaTokens, consumption.ToolSchemaTokens);
            toolNames.UnionWith(consumption.ToolNames ?? []);
        }

        // Re-capped after the union, because each step was bounded on its own and a session runs many steps.
        return new StepTotals(providerCalls,
            estimatedInputTokens,
            toolCalls,
            toolSchemaTokens,
            [.. toolNames.Take(ProviderCallBudget.MaxDistinctToolNames)]);
    }

    /// <summary>
    ///     What the provider reported, summed over the conversation's chat-run envelopes. Kind-scoped by the store
    ///     itself — <c>agent_execution_logs</c> overloads its columns across several producers, so a read that did not
    ///     filter by record kind would sum an approval audit into a node run's cost.
    /// </summary>
    private async Task<EnvelopeTotals> CollectEnvelopesAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        long? inputTokens = null;
        long? outputTokens = null;
        long? reasoningTokens = null;
        long? agentTurnMs = null;
        long? modelReadinessMs = null;
        string? servedModelName = null;

        for (var page = 0; page < MaxEnvelopePages; page++)
        {
            var envelopes = await _executionLogs
                                  .ListRunEnvelopesAsync(conversationId, EnvelopePageSize, page * EnvelopePageSize, cancellationToken)
                                  .ConfigureAwait(false);
            foreach (var envelope in envelopes)
            {
                inputTokens = Add(inputTokens, envelope.PromptTokens);
                outputTokens = Add(outputTokens, envelope.CompletionTokens);
                reasoningTokens = Add(reasoningTokens, envelope.ReasoningTokens);
                // The envelope's duration is the WHOLE chat run — provider rounds and the tool loop between them —
                // so this sum is agent-turn time, not provider time. Nothing persisted here separates the two, which
                // is why the column, the DTO member and the panel all say "turn".
                agentTurnMs = Add(agentTurnMs, envelope.DurationMs);

                // Summed beside it rather than subtracted from it: agent_turn_ms stays the whole-turn wall clock it has
                // always been, and a reader who wants the warm-equivalent time takes the difference. Null-preserving, so
                // a conversation whose turns all ran warm records null rather than a zero that would claim otherwise.
                modelReadinessMs = Add(modelReadinessMs, envelope.ModelReadinessMs);

                // The store orders newest first, so the first envelope of the first page is the model that served the
                // last turn — the receipt, as opposed to whatever the node or the agent asked for.
                servedModelName ??= Clamp(envelope.ModelName, MaxServedModelName);
            }

            if (envelopes.Count < EnvelopePageSize)
            {
                break;
            }
        }

        return new EnvelopeTotals(inputTokens, outputTokens, reasoningTokens, agentTurnMs, modelReadinessMs, servedModelName);
    }

    /// <summary>A name as the column can hold it, or null when there was none to hold.</summary>
    private static string? Clamp(string? name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return name.Length <= maxLength ? name : name[..maxLength];
    }

    private static WorkSessionStepConsumptionDetail? ReadConsumption(string detailJson)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkSessionStepConsumptionDetail>(detailJson, ConsumptionJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The names as the column stores them, or null when there were none — never an empty array, which would read
    ///     as "asked and answered nothing". A set of long names that still overruns the column is trimmed from the end
    ///     and closed with an ellipsis element, so the document stays parseable and a short list never reads as the
    ///     whole set.
    /// </summary>
    private static string? ToolNamesJson(IReadOnlyList<string> toolNames)
    {
        if (toolNames.Count == 0)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(toolNames, ConsumptionJsonOptions);
        if (json.Length <= MaxToolNamesJson)
        {
            return json;
        }

        var kept = toolNames.ToList();
        while (kept.Count > 0)
        {
            kept.RemoveAt(kept.Count - 1);
            var candidate = JsonSerializer.Serialize<IReadOnlyList<string>>([.. kept, TruncatedToolNameMarker], ConsumptionJsonOptions);
            if (candidate.Length <= MaxToolNamesJson)
            {
                return candidate;
            }
        }

        return JsonSerializer.Serialize<IReadOnlyList<string>>([TruncatedToolNameMarker], ConsumptionJsonOptions);
    }

    /// <summary>Adds an optional term, so a total stays null until something real lands in it. A null total is "nobody said", not zero.</summary>
    private static long? Add(long? total, long? term) =>
        term is { } value ? (total ?? 0) + value : total;

    private static int? Add(int? total, int? term) =>
        term is { } value ? (total ?? 0) + value : total;

    private sealed record StepTotals(int? ProviderCalls,
        long? EstimatedInputTokens,
        int? ToolCalls,
        long? ToolSchemaTokens,
        IReadOnlyList<string> ToolNames);

    private sealed record EnvelopeTotals(long? InputTokens,
        long? OutputTokens,
        long? ReasoningTokens,
        long? AgentTurnMs,
        long? ModelReadinessMs,
        string? ServedModelName);
}
