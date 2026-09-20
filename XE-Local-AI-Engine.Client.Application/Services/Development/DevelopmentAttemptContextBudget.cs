namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The window one Development attempt round is budgeted against, and how much of it is held back for the answer.
/// </summary>
/// <remarks>
///     The window is the effective per-slot context <see cref="ILocalModelProvider.GetRuntimeInfoAsync" /> reports
///     (llama.cpp's launched <c>-c</c>, as <c>LocalRuntimeWarmer</c> reads it) and the reserve is capped at a quarter
///     of it. Deriving the window from the output ceiling makes the two one number and the usable input exactly
///     0.7 × <c>maxOutputTokens</c> whatever the model serves: a 24,267-token rework round was refused against a
///     22,937 window under <c>-c 65536</c>. A window no process promised would widen the reasoning-budget clamp.
/// </remarks>
/// <param name="ContextTokens">The window the round is measured against, carried to the budgeter as <c>num_ctx</c>.</param>
/// <param name="RoundOutputTokens">The per-round output ceiling, which is also what the budgeter reserves.</param>
/// <param name="Served">Whether <paramref name="ContextTokens" /> is a window the runtime reported serving; a synthetic one is never sent as <c>num_ctx</c>.</param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct DevelopmentAttemptContextBudget(int ContextTokens, int RoundOutputTokens, bool Served)
{
    /// <summary>
    ///     The most of a known window one round may reserve for its answer.
    /// </summary>
    /// <remarks>
    ///     A quarter leaves roughly six tenths of the window for input once the estimator's 0.85 safety factor is
    ///     taken off, which is what a rework round's irreducible brief needs; reserving the configured maximum
    ///     instead produces the 0.7× ceiling.
    /// </remarks>
    private const int ReservedWindowDivisor = 4;

    /// <summary>
    ///     The pre-existing synthetic budget, kept for the paths with no launched window to read: a cloud route, and a
    ///     local runtime that reports none. Conservative on purpose — a fictional window must not also hand out a
    ///     smaller reserve.
    /// </summary>
    public static DevelopmentAttemptContextBudget Unknown(int maxOutputTokens) =>
        new(Math.Max(2048, maxOutputTokens * 2), maxOutputTokens, Served: false);

    /// <summary>
    ///     The window <paramref name="modelId" /> is actually serving, or <see cref="Unknown" /> with a warning
    ///     naming the fallback when the runtime reports none.
    /// </summary>
    /// <remarks>
    ///     The model is warmed first because the runtime reports a window only for a process it is running, and the
    ///     attempt's first send would start that process anyway. A warm that fails is swallowed: the streaming send
    ///     is the boundary that surfaces the classified provider failure, and pre-empting it here would replace a
    ///     precise error with a vague one.
    /// </remarks>
    public static async Task<DevelopmentAttemptContextBudget> ResolveAsync(ILocalModelProvider provider,
        string modelId,
        int maxOutputTokens,
        string role,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(logger);

        int? window;
        try
        {
            await provider.WarmModelAsync(modelId, cancellationToken);
            window = await provider.GetRuntimeInfoAsync(modelId, cancellationToken) is { EffectiveContextTokens: > 0 } info
                ? info.EffectiveContextTokens
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Reading the served context window for the Development {Role} model failed; the conservative fallback window is used.", role);
            window = null;
        }

        if (window is not { } served)
        {
            var fallback = Unknown(maxOutputTokens);
            logger.LogWarning(
                "The Development {Role} model reports no served context window, so this attempt is budgeted against a conservative fallback of {Window} token(s) reserving {Reserved} for output. A round whose brief does not fit will be refused before the provider is called.",
                role,
                fallback.ContextTokens,
                fallback.RoundOutputTokens);
            return fallback;
        }

        // Math.Max on the upper bound, not decoration: a project configured with a maximum-tokens budget of 0 would
        // otherwise hand Math.Clamp a range whose low is above its high, which throws.
        return new DevelopmentAttemptContextBudget(served,
            Math.Clamp(served / ReservedWindowDivisor, 1, Math.Max(1, maxOutputTokens)),
            Served: true);
    }
}
