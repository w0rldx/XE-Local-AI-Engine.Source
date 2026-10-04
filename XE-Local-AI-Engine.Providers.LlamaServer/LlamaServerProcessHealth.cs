namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Per-process health snapshot for one running <c>(model, role)</c> llama-server, aggregated by the supervisor
///     into the single provider-level <c>ModelProviderHealth</c>.
/// </summary>
public sealed class LlamaServerProcessHealth
{
    /// <summary>Model the process serves.</summary>
    public required string ModelName { get; init; }

    /// <summary>Role the process serves.</summary>
    public required ModelRole Role { get; init; }

    /// <summary>Whether the process answered its health probe.</summary>
    public required bool IsResponsive { get; init; }

    /// <summary>A sanitized, user-safe diagnostic line (no internal paths/secrets).</summary>
    public required string Detail { get; init; }

    /// <summary>
    ///     Whether the OS process behind this entry is GONE.
    /// </summary>
    /// <remarks>
    ///     Distinct from <see cref="IsResponsive" />, which is also false for a process that is alive but still loading
    ///     or wedged: an exited entry holds no VRAM, no port and no loaded-process slot, and lingers in the table only
    ///     until the idle reaper collects it. A caller that decides capacity or routing from this snapshot must treat
    ///     it as NOT running; one that merely reports health shows it as the exited process it is. A trailing optional
    ///     positional, so an existing construction needs no change.
    /// </remarks>
    public bool HasExited { get; init; }

    /// <summary>Whether in-flight inference holds a lease on the process right now.</summary>
    public bool IsBusy { get; init; }

    /// <summary>When the process was last ensured, reused or leased; <see langword="null" /> when the snapshot source does not track it.</summary>
    public DateTimeOffset? LastUsedUtc { get; init; }

    /// <summary>
    ///     Whether the process was loaded only for a transient request (an AI Assist draft) and has not been used
    ///     interactively since, so it is reaped after the short transient idle lifetime.
    /// </summary>
    public bool IsTransient { get; init; }

    /// <summary>The context window the running server reported from <c>/props</c>; <see langword="null" /> when it could not be read.</summary>
    public int? EffectiveContextTokens { get; init; }

    /// <summary>Whether the launch pinned Mixture-of-Experts weights to system RAM, so generation streams experts from RAM.</summary>
    public bool ExpertsOffloaded { get; init; }
}
