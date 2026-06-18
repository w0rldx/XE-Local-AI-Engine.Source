namespace XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Shared Quartz identity constants for scheduled-job dispatch. The management service  builds every
///     <c>JobKey</c> in the <see cref="Group" /> group and stamps the definition id into the <c>JobDataMap</c> under
///     <see cref="ScheduledJobIdKey" />; the dispatch jobs read it back at fire time.
/// </summary>
public static class SchedulerJobKeys
{
    /// <summary>
    ///     <c>JobDataMap</c> key under which the scheduled job definition id (a <see cref="System.Guid" /> string) is
    ///     stored. With <c>UseProperties = true</c> the map is string-only, so the value is the <c>Guid</c>'s string form.
    /// </summary>
    public const string ScheduledJobIdKey = "scheduledJobId";

    /// <summary>
    ///     Quartz <c>JobKey</c> group that all scheduled-job definitions share. A stable per-definition <c>JobKey</c> name
    ///     within this group keeps persistent identities aligned across restarts.
    /// </summary>
    public const string Group = "scheduled-jobs";

    /// <summary>
    ///     Optional per-fire trigger <c>JobDataMap</c> key carrying a use-case override for a model-fit recommendation
    ///     refresh. A manual <c>TriggerNowAsync</c> fire may stamp this onto the trigger's data map so the run produces the
    ///     selected use-case instead of the definition's baked one. The recurring (cron) fire never sets it, so a scheduled
    ///     run is unchanged. The dispatcher merges ONLY this whitelisted key over the stored parameters — no other key from
    ///     the per-fire map can override a stored parameter.
    /// </summary>
    public const string ModelFitUseCaseOverrideKey = "modelFitUseCaseOverride";

    /// <summary>
    ///     Optional per-fire trigger <c>JobDataMap</c> key carrying a recommendation breadth (<c>--limit</c>) override for
    ///     a model-fit recommendation refresh. Like <see cref="ModelFitUseCaseOverrideKey" /> it is set only on a manual
    ///     <c>TriggerNowAsync</c> fire (never on the recurring cron fire) and the dispatcher merges ONLY this whitelisted
    ///     key over the stored parameters — written back as a JSON number so the handler's numeric <c>limit</c> parse is
    ///     unchanged. The value is validated to the supported <c>1..50</c> range before it is stamped.
    /// </summary>
    public const string ModelFitLimitOverrideKey = "modelFitLimitOverride";

    /// <summary>
    ///     Optional per-fire trigger <c>JobDataMap</c> key carrying a quant override (e.g. <c>Q5_K_M</c>) for a model-fit
    ///     recommendation refresh. Like the other model-fit override keys it is set only on a manual <c>TriggerNowAsync</c>
    ///     fire (never on the recurring cron fire) and the dispatcher merges ONLY this whitelisted key over the stored
    ///     parameters — replacing the default <c>Q4_K_M</c> the advisor would otherwise estimate against.
    /// </summary>
    public const string ModelFitQuantOverrideKey = "modelFitQuantOverride";

    /// <summary>
    ///     Optional per-fire trigger <c>JobDataMap</c> key carrying a context-window target the advisor's KV-cache fit is
    ///     sized against. Like the other model-fit override keys it is set only on a manual <c>TriggerNowAsync</c> fire
    ///     (never on the recurring cron fire), validated to ≥256 before it is stamped, and written back as a JSON number so
    ///     the handler's numeric <c>ctxTarget</c> parse is unchanged.
    /// </summary>
    public const string ModelFitCtxTargetOverrideKey = "modelFitCtxTargetOverride";
}
