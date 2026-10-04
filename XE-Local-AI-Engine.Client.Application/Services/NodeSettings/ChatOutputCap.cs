namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>The node's chat output-cap variant (<see cref="StoredNodeSettings.ChatOutputCapMode" />) and its ceiling.</summary>
public sealed record ChatOutputCap
{
    /// <summary><c>cap</c>, <c>notice</c> or <c>off</c>.</summary>
    public required string Mode { get; init; }

    /// <summary>The ceiling on the cap, which is otherwise half the launched window.</summary>
    public required int MaxTokens { get; init; }

    /// <summary>Whether a turn that ends on the cap or the window carries a "stopped at length" notice.</summary>
    public bool NotifiesOnLength => Mode is not StoredNodeSettings.ChatOutputCapModeOff;

    /// <summary>The output cap for a launched window: half of it, at most <see cref="MaxTokens" />; null when uncapped or the window is unknown.</summary>
    public int? TokensFor(int? windowTokens) =>
        Mode is StoredNodeSettings.ChatOutputCapModeCap && windowTokens is { } window && window > 0
            ? Math.Max(1, Math.Min(window / 2, MaxTokens))
            : null;
}
