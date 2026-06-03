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
}
