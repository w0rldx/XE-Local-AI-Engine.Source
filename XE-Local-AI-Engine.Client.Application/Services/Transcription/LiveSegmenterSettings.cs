namespace XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     The three numbers that decide when a live lane submits audio and when it commits what came back.
/// </summary>
/// <remarks>
///     Only <see cref="MaxWindowSeconds" /> is a user setting. <see cref="TailGuardMs" /> and <see cref="TickMs" />
///     are internal: they trade partial-text latency against re-transcription cost and mean nothing to an operator,
///     so they are settable for tests and never surfaced.
/// </remarks>
public sealed record LiveSegmenterSettings
{
    /// <summary>The smallest window an operator may ask for; below this the model has too little context to punctuate.</summary>
    public const int MinWindowSeconds = 2;

    /// <summary>The largest window an operator may ask for; above this a committed segment is too stale to read live.</summary>
    public const int MaxAllowedWindowSeconds = 10;

    /// <summary>How much uncommitted audio forces a commit, in seconds. Clamped to 2..10.</summary>
    public int MaxWindowSeconds { get; init; } = 5;

    /// <summary>
    ///     How far behind the end of the received audio a segment must end before it is durable.
    /// </summary>
    /// <remarks>
    ///     A segment that touches the end of the window is a segment the model cut short because the audio stopped,
    ///     not because the speaker did. Holding the last fraction of a second back is what stops a word being
    ///     committed in two halves.
    /// </remarks>
    public int TailGuardMs { get; init; } = 800;

    /// <summary>How much new audio time passes between ordinary submissions.</summary>
    public int TickMs { get; init; } = 1_000;

    /// <summary>Builds the settings for a session from its stored window preference, clamping it into the allowed range.</summary>
    /// <param name="maxWindowSeconds">The operator's preference, or <see langword="null" /> for the default.</param>
    public static LiveSegmenterSettings FromSessionConfig(int? maxWindowSeconds) =>
        maxWindowSeconds is { } requested
            ? new LiveSegmenterSettings { MaxWindowSeconds = Math.Clamp(requested, MinWindowSeconds, MaxAllowedWindowSeconds) }
            : new LiveSegmenterSettings();
}
