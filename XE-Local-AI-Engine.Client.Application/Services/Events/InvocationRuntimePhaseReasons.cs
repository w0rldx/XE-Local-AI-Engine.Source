namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>The wire values of <see cref="InvocationRuntimePhaseDetail.Reason" />; the SPA keys its loading text on them.</summary>
public static class InvocationRuntimePhaseReasons
{
    public const string FirstLoad = "first-load";
    public const string IdleUnloaded = "idle-unloaded";
    public const string EvictedForCap = "evicted-for-cap";
    public const string EvictedForMemory = "evicted-for-memory";
}
