namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     Options for the process-wide GPU-load admission gate (AUD4-06). The only knob is the bounded max-wait: how long a
///     GPU-backed load will wait for the gate before surfacing a typed timeout rather than hanging a chat turn forever.
///     It is a backstop — the size-aware readiness timeouts that bound the current holder already make an indefinite hold
///     impossible — so the default is generous enough to cover a legitimately slow big-model load.
/// </summary>
public sealed record GpuModelLoadAdmissionOptions
{
    /// <summary>The bounded wait before a queued GPU load surfaces a typed admission-timeout. Must be positive.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Fails fast on a non-positive max-wait so a misconfiguration surfaces at startup, not as a hang.</summary>
    public void Validate()
    {
        if (MaxWait <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(MaxWait)} must be positive (was {MaxWait}).");
        }
    }
}
