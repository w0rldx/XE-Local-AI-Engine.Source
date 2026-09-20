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

    /// <summary>Hard upper bound on simultaneously-Enabled actions for a single agent.</summary>
    public int MaxEnabledActions { get; set; } = 20;
}
