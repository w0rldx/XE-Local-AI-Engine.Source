namespace XE_Local_AI_Engine.AI.Agent.Chat;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Contracts.Telemetry;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Innermost pipeline hop (below <c>UseFunctionInvocation</c>) that re-budgets EVERY raw provider round against the
///     effective context window and enforces the invocation's cumulative provider-call ceilings.
/// </summary>
/// <remarks>
///     The outer invocation runner budgets only its two OUTER history-growth points, so the inner tool-calling loop and
///     MAF participant rounds are visible only here. Gated on an ambient <see cref="ProviderCallBudget" /> scope seeded
///     per invocation by the runner; with no scope the hop is a transparent pass-through.
///     See docs/wiki/04-agent-mode.md ("Provider-boundary budgeting").
/// </remarks>
internal sealed class ProviderCallBudgetChatClient : DelegatingChatClient
{
    private static readonly Meter Meter = new(TelemetrySourceNames.Agent, "1.0.0");
    private static readonly Counter<long> ProviderRoundsCounter = Meter.CreateCounter<long>("xe.agent.provider_rounds", description: "Raw provider rounds observed at the budget boundary.");
    private static readonly Counter<long> MessagesDroppedCounter = Meter.CreateCounter<long>("xe.agent.budget.messages_dropped", description: "History messages dropped by per-round budgeting.");

    private static readonly Counter<long> ToolResultsTruncatedCounter =
        Meter.CreateCounter<long>("xe.agent.budget.tool_results_truncated", description: "Oversized tool results excerpted by per-round budgeting.");

    private static readonly Counter<long> CeilingExceededCounter =
        Meter.CreateCounter<long>("xe.agent.budget.ceiling_exceeded", description: "Invocations terminated for exceeding a cumulative provider-call ceiling.");

    private static readonly Counter<long> ContextWindowExceededCounter = Meter.CreateCounter<long>("xe.agent.budget.context_window_exceeded",
        description: "Provider rounds rejected because the irreducible message set still exceeded the context window.");

    // The Ollama num_ctx option key the invocation factory writes onto ChatOptions.AdditionalProperties when a per-send
    // context window is set; read here so the per-round window matches the window the provider is actually launched with.
    private const string NumCtxKey = "num_ctx";

    private readonly ILogger<ProviderCallBudgetChatClient> _logger;
    private readonly ITokenEstimatorCalibrationStore _calibrationStore;

    public ProviderCallBudgetChatClient(IChatClient innerClient,
        ILogger<ProviderCallBudgetChatClient> logger,
        ITokenEstimatorCalibrationStore? calibrationStore = null)
        : base(innerClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _calibrationStore = calibrationStore ?? new TokenEstimatorCalibrationStore();
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var round = ApplyBudget(messages, options);
        var budget = ProviderCallBudget.Current;
        var startedTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var response = await base.GetResponseAsync(round.Messages, round.Options, cancellationToken).ConfigureAwait(false);
            RecordObservedUsage(round, response.Usage?.InputTokenCount);
            return response;
        }
        finally
        {
            budget?.RecordProviderRoundElapsed(Stopwatch.GetElapsedTime(startedTimestamp));
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // Budget (and enforce the cumulative ceiling) BEFORE the first chunk is pulled, so a ceiling breach fails the
        // round up front rather than after streaming begins.
        var round = ApplyBudget(messages, options);
        var budget = ProviderCallBudget.Current;
        var startedTimestamp = Stopwatch.GetTimestamp();

        // Terminal usage arrives as a UsageContent on one of the streamed updates (llama.cpp reports it on the final
        // chunk). Last one wins, matching the invocation runner's own reading of the same signal.
        long? observedInputTokens = null;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(round.Messages, round.Options, cancellationToken).ConfigureAwait(false))
            {
                if (update.Contents is { Count: > 0 } contents)
                {
                    foreach (var content in contents)
                    {
                        if (content is UsageContent usage && usage.Details.InputTokenCount is { } inputTokens)
                        {
                            observedInputTokens = inputTokens;
                        }
                    }
                }

                yield return update;
            }
        }
        finally
        {
            // A streamed call is pull-based: include the complete enumerator lifetime, including any consumer
            // backpressure while the provider request remains open. This is provider-round elapsed time, not CPU time.
            budget?.RecordProviderRoundElapsed(Stopwatch.GetElapsedTime(startedTimestamp));

            // Recorded here rather than after the loop so an abandoned or faulted enumeration still contributes the
            // usage it did report; a round that reported none contributes nothing.
            RecordObservedUsage(round, observedInputTokens);
        }
    }

    /// <summary>
    ///     Feeds one real round back into the estimator's per-model calibration: what the provider counted for the
    ///     prompt, against what this hop estimated for the very message set it sent.
    /// </summary>
    /// <remarks>
    ///     The only place in the process that holds both numbers for the SAME request. Only budgeted rounds carry a
    ///     model name (see <see cref="ApplyBudget" />), so the pass-through paths record nothing, and neither do the
    ///     summarizer and the other side calls that build their own per-run client. The store applies its own minimum
    ///     sample size and bounds; everything here is a null check.
    /// </remarks>
    private void RecordObservedUsage(in BudgetedRound round, long? observedInputTokens)
    {
        if (round.ModelName is not { } modelName || observedInputTokens is not { } observed || observed <= 0)
        {
            return;
        }

        _calibrationStore.RecordObservedUsage(modelName, round.EstimatedInputTokens, observed);
    }

    /// <summary>
    ///     Applies the per-round input budget, registers the round against the cumulative ceilings, and returns the
    ///     (possibly reduced) message list to send plus the options to send it with.
    /// </summary>
    /// <remarks>
    ///     The options are the caller's own instance unless the reasoning budget had to be narrowed against the room
    ///     this round's input leaves (see <see cref="NarrowReasoningBudget" />); the model name and estimate are what
    ///     <see cref="RecordObservedUsage" /> feeds back into calibration. A pass-through — both inputs unchanged, no
    ///     model name, nothing recorded — when no ambient budget scope is present. Throws
    ///     <see cref="ProviderCallBudgetExceededException" /> when a cumulative ceiling trips.
    /// </remarks>
    private BudgetedRound ApplyBudget(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var budget = ProviderCallBudget.Current;
        if (budget is null)
        {
            return new BudgetedRound(messages, options, ModelName: null, EstimatedInputTokens: 0);
        }

        var budgetOptions = budget.Options;
        var materialized = messages as IReadOnlyList<ChatMessage> ?? [.. messages];

        var window = ResolveContextWindow(options, budgetOptions);
        var reserved = ResolveReservedOutputTokens(options, budgetOptions);

        // Two comparison-only margins: EstimateSafetyFactor of the window (the char heuristic under-counts ~10% on
        // markdown/JSON, and an under-count at the edge is a rejection, not a trim), then this model's own correction.
        var modelId = options?.ModelId;
        var observedCorrection = _calibrationStore.ResolveObservedCorrection(modelId);
        var effectiveWindow = Math.Max(TokenEstimatorCalibrationStore.ApplyEstimateMargins(window, observedCorrection) - reserved, 0);
        var charsPerToken = _calibrationStore.ResolveDivisor(modelId);
        // Instructions AND tool definitions (name + description + JSON schema) are fixed per-round input the model never
        // sees as a droppable message; folding both in stops a tool-heavy agent rounding an over-window request through.
        var toolSchemaTokens = ProviderMessageTokenEstimator.EstimateTools(options?.Tools, charsPerToken);
        var instructionsTokens = ProviderMessageTokenEstimator.EstimateTokens(options?.Instructions, charsPerToken)
                                 + toolSchemaTokens;

        var result = ProviderCallBudgeter.Budget(materialized, instructionsTokens, effectiveWindow, budgetOptions, charsPerToken);

        ProviderRoundsCounter.Add(1);
        if (result.Trimmed)
        {
            MessagesDroppedCounter.Add(result.MessagesDropped);
            ToolResultsTruncatedCounter.Add(result.ToolResultsTruncated);
            _logger.LogDebug(
                "Provider-round context budgeted: dropped {Dropped} message(s), truncated {Truncated} tool result(s) ({Chars} chars), estimated tokens {Before} -> {After}, window {Window} reserving {Reserved} (still over window: {Overflow}).",
                result.MessagesDropped,
                result.ToolResultsTruncated,
                result.CharsTruncated,
                result.EstimatedTokensBefore,
                result.EstimatedTokensAfter,
                window,
                reserved,
                result.ExceedsWindow);
        }

        // An irreducible round (the pinned set alone is over the window) is failed HERE with a classified, sanitized
        // error; the ceiling registration below is skipped deliberately, because this round never reaches the provider.
        if (result.ExceedsWindow)
        {
            ContextWindowExceededCounter.Add(1);
            _logger.LogWarning(
                "Provider round rejected: irreducible message set still exceeds the context window (~{Tokens} estimated input token(s) over an effective window of {Window}); failing the round before the provider is called.",
                result.EstimatedTokensAfter,
                effectiveWindow);
            throw new ProviderContextWindowExceededException(result.EstimatedTokensAfter, effectiveWindow);
        }

        try
        {
            budget.RegisterProviderRound(result.EstimatedTokensAfter,
                toolSchemaTokens,
                result.MessagesDropped,
                result.ToolResultsTruncated,
                result.CharsTruncated);
        }
        catch (ProviderCallBudgetExceededException)
        {
            CeilingExceededCounter.Add(1);
            _logger.LogWarning("Invocation exceeded its cumulative provider-call budget after {Calls} round(s) and ~{Tokens} estimated input token(s); stopping the turn.",
                budget.ProviderCalls,
                budget.CumulativeInputTokens);
            throw;
        }

        return new BudgetedRound(result.Messages,
            NarrowReasoningBudget(options, window, result.EstimatedTokensAfter),
            // Only a named model can be calibrated; an unnamed round is sent but teaches nothing.
            string.IsNullOrWhiteSpace(modelId) ? null : modelId,
            result.EstimatedTokensAfter);
    }

    /// <summary>
    ///     One budgeted provider round: what to send, what to send it with, and the (model, estimated input tokens)
    ///     pair the response's reported usage is compared against once the round comes back.
    /// </summary>
    private readonly record struct BudgetedRound(IEnumerable<ChatMessage> Messages, ChatOptions? Options, string? ModelName, int EstimatedInputTokens);

    /// <summary>
    ///     Narrows the llama.cpp thinking-budget marker to the room THIS round's input actually leaves, when the turn
    ///     carries one.
    /// </summary>
    /// <remarks>
    ///     The provider-side clamp sees only the launched window, so on a long conversation it still permits a budget
    ///     larger than the tokens left after the prompt — and a reasoning phase that eats the remainder returns no
    ///     answer, the failure the budget exists to prevent. Half the remainder, matching the provider clamp's split.
    ///     Returns <paramref name="options" /> unchanged when there is no marker or it is already smaller, so nothing
    ///     is cloned on the common path.
    /// </remarks>
    private static ChatOptions? NarrowReasoningBudget(ChatOptions? options, int window, int estimatedInputTokens)
    {
        if (options?.AdditionalProperties is not { } properties
            || !properties.TryGetValue(ReasoningOptionsResolver.LlamaReasoningBudgetMarkerKey, out var raw)
            || !TryToInt(raw, out var budgetTokens)
            || budgetTokens <= 0)
        {
            return options;
        }

        var remaining = Math.Max(window - estimatedInputTokens, 0);
        var allowed = Math.Max(remaining / 2, 1);
        if (allowed >= budgetTokens)
        {
            return options;
        }

        var narrowed = options.Clone();
        narrowed.AdditionalProperties = new AdditionalPropertiesDictionary(properties)
        {
            [ReasoningOptionsResolver.LlamaReasoningBudgetMarkerKey] = allowed
        };
        return narrowed;
    }

    private static int ResolveContextWindow(ChatOptions? options, ProviderCallBudgetOptions budgetOptions)
    {
        if (options?.AdditionalProperties is { } properties
            && properties.TryGetValue(NumCtxKey, out var raw)
            && TryToInt(raw, out var numCtx)
            && numCtx > 0)
        {
            return numCtx;
        }

        return budgetOptions.DefaultContextTokens;
    }

    private static int ResolveReservedOutputTokens(ChatOptions? options, ProviderCallBudgetOptions budgetOptions)
    {
        var requestedOutput = options?.MaxOutputTokens is { } maxOutput && maxOutput > 0 ? maxOutput : 0;
        return Math.Max(budgetOptions.ReservedOutputTokenFloor, requestedOutput);
    }

    private static bool TryToInt(object? value, out int result)
    {
        switch (value)
        {
            case int intValue:
                result = intValue;
                return true;
            case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
                result = (int)longValue;
                return true;
            default:
                result = 0;
                return value is not null && int.TryParse(value.ToString(), out result);
        }
    }
}
