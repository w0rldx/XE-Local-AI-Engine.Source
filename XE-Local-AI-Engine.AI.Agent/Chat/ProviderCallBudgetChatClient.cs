namespace XE_Local_AI_Engine.AI.Agent.Chat;

using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     Innermost pipeline hop (below <c>UseFunctionInvocation</c>) that re-budgets EVERY raw provider round against the
///     effective context window and enforces the invocation's cumulative provider-call ceilings. The outer invocation
///     runner budgets only its two OUTER history-growth points (initial seed and each approval-resume); the autonomous
///     tool-calling loop inside <c>FunctionInvokingChatClient</c> appends tool results and calls the provider again
///     without the runner seeing it, and MAF participant turns are likewise invisible to the runner — so this hop is the
///     only place that sees, and can bound, those inner rounds.
///     <para>
///         Budgeting is gated on an ambient <see cref="ProviderCallBudget" /> scope (seeded per invocation by the
///         runner); when none is present (the eval / preview-workflow runners drive the same shared client without one)
///         the client is a transparent pass-through, so those paths are byte-identical to before.
///     </para>
/// </summary>
internal sealed class ProviderCallBudgetChatClient : DelegatingChatClient
{
    private static readonly Meter Meter = new("XE.LocalAiEngine.AI.Agent", "1.0.0");
    private static readonly Counter<long> ProviderRoundsCounter = Meter.CreateCounter<long>("xe.agent.provider_rounds", description: "Raw provider rounds observed at the budget boundary.");
    private static readonly Counter<long> MessagesDroppedCounter = Meter.CreateCounter<long>("xe.agent.budget.messages_dropped", description: "History messages dropped by per-round budgeting.");
    private static readonly Counter<long> ToolResultsTruncatedCounter = Meter.CreateCounter<long>("xe.agent.budget.tool_results_truncated", description: "Oversized tool results excerpted by per-round budgeting.");
    private static readonly Counter<long> CeilingExceededCounter = Meter.CreateCounter<long>("xe.agent.budget.ceiling_exceeded", description: "Invocations terminated for exceeding a cumulative provider-call ceiling.");
    private static readonly Counter<long> ContextWindowExceededCounter = Meter.CreateCounter<long>("xe.agent.budget.context_window_exceeded", description: "Provider rounds rejected because the irreducible message set still exceeded the context window.");

    // The Ollama num_ctx option key the invocation factory writes onto ChatOptions.AdditionalProperties when a per-send
    // context window is set; read here so the per-round window matches the window the provider is actually launched with.
    private const string NumCtxKey = "num_ctx";

    private readonly ILogger<ProviderCallBudgetChatClient> _logger;

    public ProviderCallBudgetChatClient(IChatClient innerClient, ILogger<ProviderCallBudgetChatClient> logger)
        : base(innerClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var budgeted = ApplyBudget(messages, options);
        return base.GetResponseAsync(budgeted, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // Budget (and enforce the cumulative ceiling) BEFORE the first chunk is pulled, so a ceiling breach fails the
        // round up front rather than after streaming begins.
        var budgeted = ApplyBudget(messages, options);
        await foreach (var update in base.GetStreamingResponseAsync(budgeted, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>
    ///     Applies the per-round input budget and registers the round against the cumulative ceilings. Returns the
    ///     (possibly reduced) message list to send. A pass-through (returns the input unchanged) when no ambient budget
    ///     scope is present. Throws <see cref="ProviderCallBudgetExceededException" /> when a cumulative ceiling trips.
    /// </summary>
    private IEnumerable<ChatMessage> ApplyBudget(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var budget = ProviderCallBudget.Current;
        if (budget is null)
        {
            return messages;
        }

        var budgetOptions = budget.Options;
        var materialized = messages as IReadOnlyList<ChatMessage> ?? [.. messages];

        var window = ResolveContextWindow(options, budgetOptions);
        var reserved = ResolveReservedOutputTokens(options, budgetOptions);
        var effectiveWindow = Math.Max(window - reserved, 0);
        // Instructions AND the tool definitions (name + description + JSON schema) are fixed per-round input the model
        // never sees as a droppable message but which still counts against the window — folding both into the overhead
        // stops a tool-heavy agent from under-estimating and rounding an over-window request through.
        var instructionsTokens = ProviderMessageTokenEstimator.EstimateTokens(options?.Instructions)
                                 + ProviderMessageTokenEstimator.EstimateTools(options?.Tools);

        var result = ProviderCallBudgeter.Budget(materialized, instructionsTokens, effectiveWindow, budgetOptions);

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

        // A single round whose pinned set alone still exceeds the window is irreducible: no further trimming can shrink
        // it (the budgeter already excerpted and dropped everything it may). Sending it would overrun the model's
        // launched context window or be rejected deep inside the provider with an opaque error, so fail it HERE — before
        // the inner client is called, in both the sync and streaming paths (both route through ApplyBudget) — with a
        // classified, sanitized error. The cumulative-ceiling registration below is intentionally skipped: this round
        // never reaches the provider.
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
            budget.RegisterProviderRound(result.EstimatedTokensAfter);
        }
        catch (ProviderCallBudgetExceededException)
        {
            CeilingExceededCounter.Add(1);
            _logger.LogWarning("Invocation exceeded its cumulative provider-call budget after {Calls} round(s) and ~{Tokens} estimated input token(s); stopping the turn.",
                budget.ProviderCalls,
                budget.CumulativeInputTokens);
            throw;
        }

        return result.Messages;
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
