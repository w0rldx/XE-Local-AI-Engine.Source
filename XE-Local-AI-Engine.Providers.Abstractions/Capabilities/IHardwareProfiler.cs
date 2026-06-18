namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Cross-platform probe for the host's inference-relevant hardware (RAM / VRAM / GPU vendor / CPU / free disk).
///     Provider-neutral and free of any <c>HostAgent.*</c> dependency (Lane C↔D sequencing gate, plan §7.1/§13).
/// </summary>
/// <remarks>
///     Implementations cache the last <see cref="HardwareProfile" /> in memory; pass <paramref name="forceRefresh" />
///     to re-probe. Probing never throws on a missing GPU/tool — it degrades to <see cref="HardwareProfile.VramKnown" />
///     <see langword="false" /> (the CPU-mode floor).
/// </remarks>
public interface IHardwareProfiler
{
    /// <summary>Returns the current hardware profile, re-probing when <paramref name="forceRefresh" /> is set.</summary>
    /// <param name="forceRefresh">When <see langword="true" />, bypasses the in-memory cache and re-probes.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<HardwareProfile> GetProfileAsync(bool forceRefresh, CancellationToken ct);
}
