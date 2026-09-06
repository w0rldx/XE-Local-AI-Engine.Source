namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The window one Development attempt round is budgeted against, and how much of it is held back for the answer.
///     <para>
///         Both attempt roles used to invent the window as <c>2 × maxOutputTokens</c> and reserve <c>maxOutputTokens</c>
///         of it, which made the usable input budget exactly <c>0.7 × maxOutputTokens</c> — after the estimator's 0.85
///         safety factor — for EVERY model, whatever context it was actually launched with. Measured live on
///         2026-09-02: a routed rework round of ~24,267 estimated input tokens was refused against an "effective window"
///         of 22,937 while llama-server was serving the same model with <c>-c 65536</c>. The window and the reserve were
///         two names for the same number, so no model could ever be sized correctly.
///     </para>
///     <para>
///         The window now comes from the same place the chat and work-session lanes read it —
///         <see cref="ILocalModelProvider.GetRuntimeInfoAsync" />'s effective per-slot context, which llama.cpp reports
///         as the launched <c>-c</c> (mirrors <c>LocalRuntimeWarmer.ResolveEffectiveContextTokensAsync</c>) — and the
///         reserve is capped at a quarter of it, so a coder round's brief, policy and routed feedback have most of the
///         window to fit in rather than competing with an output ceiling as large as half the context.
///     </para>
/// </summary>
/// <param name="ContextTokens">The window the round is measured against, carried to the budgeter as <c>num_ctx</c>.</param>
/// <param name="RoundOutputTokens">The per-round output ceiling, which is also what the budgeter reserves.</param>
/// <param name="Served">
///     Whether <paramref name="ContextTokens" /> is the window the runtime reported serving. A synthetic window is
///     NOT written onto the request as <c>num_ctx</c> — that key is also what the llama.cpp reasoning-budget clamp
///     sizes against, and a number no process promised would widen a clamp rather than tighten it.
/// </param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct DevelopmentAttemptContextBudget(int ContextTokens, int RoundOutputTokens, bool Served)
{
    /// <summary>
    ///     The most of a KNOWN window one round may reserve for its answer. A quarter leaves roughly six tenths of the
    ///     window for input once the estimator's safety factor is taken off, which is what a rework round's irreducible
    ///     brief needs; reserving the configured maximum instead is what produced the 0.7× ceiling.
    /// </summary>
    private const int ReservedWindowDivisor = 4;

    /// <summary>
    ///     The pre-existing synthetic budget, kept for the paths with no launched window to read: a cloud route, and a
    ///     local runtime that reports none. Conservative on purpose — a fictional window must not also hand out a
    ///     smaller reserve.
    /// </summary>
    public static DevelopmentAttemptContextBudget Unknown(int maxOutputTokens) =>
        new(Math.Max(2048, maxOutputTokens * 2), maxOutputTokens, Served: false);

    /// <summary>
    ///     The window <paramref name="modelId" /> is actually serving, or <see cref="Unknown" /> with a warning naming
    ///     the fallback when the runtime reports none.
    ///     <para>
    ///         The model is warmed first because the runtime only reports a window for a process it is running, and the
    ///         attempt's first send would start that process anyway — so this pays no cost the round was not already
    ///         going to pay. A warm that fails is swallowed: the streaming send is the boundary that surfaces the
    ///         classified provider failure, and pre-empting it here would replace a precise error with a vague one.
    ///     </para>
    /// </summary>
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
            await provider.WarmModelAsync(modelId, cancellationToken).ConfigureAwait(false);
            window = await provider.GetRuntimeInfoAsync(modelId, cancellationToken).ConfigureAwait(false) is { EffectiveContextTokens: > 0 } info
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
