namespace XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Options for the bounded playbook-action store.
/// </summary>
/// <remarks>
///     <see cref="MaxEnabledActions" /> is the hard cap on Enabled actions per agent: at the cap the eval-gated
///     promote path answers CapReached (409) and a manual enable is rejected (400), so prompt bloat is bounded with
///     no silent eviction.
/// </remarks>
public sealed class PlaybookActionOptions
{
    public const string Section = "PlaybookActions";

    /// <summary>
    ///     Longest <c>Behavior</c> any write path persists, in characters. Sized to the injection budget: eight actions
    ///     at the limit estimate to the 2000-token <c>PlaybookRetrieval:MaxInjectedMemoryTokens</c> default.
    /// </summary>
    public const int MaxBehaviorLength = 1000;

    /// <summary>Longest non-null <c>TriggerCondition</c> any write path persists, in characters.</summary>
    public const int MaxTriggerConditionLength = 500;

    /// <summary>Hard upper bound on simultaneously-Enabled actions for a single agent.</summary>
    public int MaxEnabledActions { get; set; } = 20;
}
