namespace XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Options for the bounded playbook-action store (Playbook P5, plan §5). <see cref="MaxEnabledActions" /> is the hard
///     cap on Enabled actions per agent: the eval-gated promote path returns CapReached (409) at the cap and manual
///     enable is rejected (400), so prompt bloat is bounded with no silent eviction.
/// </summary>
public sealed class PlaybookActionOptions
{
    public const string Section = "PlaybookActions";

    /// <summary>Hard upper bound on simultaneously-Enabled actions for a single agent.</summary>
    public int MaxEnabledActions { get; set; } = 20;
}
