namespace XE_Local_AI_Engine.Providers.WhisperCpp;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     The pure hardware-fit rule behind the "recommended" Whisper model. No I/O and no hardware probe of its own — the
///     caller supplies the <see cref="HardwareProfile" /> the shared profiler already produced, which is what keeps the
///     rule a table test rather than something only a live box can exercise.
/// </summary>
/// <remarks>
///     <para>
///         <b>The rule selects a ROW, not a tier.</b> Three catalogue rows share
///         <see cref="WhisperModelTier.LargeTurbo" />, so "the largest tier that fits" names no single row and two
///         readers would write two different functions. The walk keeps the LAST entry in
///         <see cref="WhisperModelCatalog.Models" /> (ordered ascending by footprint) whose approximate footprint times
///         <see cref="HeadroomFactor" /> fits the budget. <see cref="WhisperModelEntry.Tier" /> is consulted for exactly
///         one thing: the CPU ceiling.
///     </para>
///     <para>
///         Footprint and budget always come from the same column. A GPU backend compares approximate VRAM against the
///         VRAM budget; a CPU backend, and a GPU backend whose VRAM could not be measured, compare approximate RAM
///         against the RAM budget. A VRAM figure is never weighed against a RAM budget.
///     </para>
/// </remarks>
public static class WhisperModelRecommendation
{
    /// <summary>Fraction of headroom required beyond a row's approximate footprint before it may be recommended.</summary>
    public const double HeadroomFactor = 1.25;

    /// <summary>
    ///     Returns the largest catalogue row that fits <paramref name="profile" /> for <paramref name="backend" />, or
    ///     the smallest row when nothing fits. Never <see langword="null" />: a node with too little memory is still
    ///     offered the entry-level model rather than nothing at all.
    /// </summary>
    /// <param name="profile">The host's measured hardware profile.</param>
    /// <param name="backend">
    ///     The backend the runtime will actually serve. On <see cref="WhisperBackend.Cpu" /> no row above
    ///     <see cref="WhisperModelTier.Small" /> is ever returned — a large model on CPU is slower than real time
    ///     however much memory the box has.
    /// </param>
    public static WhisperModelEntry Recommend(HardwareProfile profile, WhisperBackend backend)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var models = WhisperModelCatalog.Models;
        var isCpuBackend = backend == WhisperBackend.Cpu;

        // A GPU backend sizes against VRAM only when VRAM was actually measured. An unmeasurable device is not
        // evidence of a large one, so it degrades to the RAM column AND the RAM budget together.
        var useVramColumn = !isCpuBackend && profile.VramKnown && (profile.AvailableVramBytes ?? profile.VramBytes) is not null;
        var budgetBytes = useVramColumn
            ? profile.AvailableVramBytes ?? profile.VramBytes!.Value
            : profile.AvailableRamBytes;

        WhisperModelEntry? best = null;
        foreach (var entry in models)
        {
            // The CPU ceiling is a TIER check, not a budget check: a 64 GB box must not be handed a turbo row.
            if (isCpuBackend && entry.Tier > WhisperModelTier.Small)
            {
                continue;
            }

            var footprintBytes = useVramColumn ? entry.ApproximateVramBytes : entry.ApproximateRamBytes;
            if ((long)(footprintBytes * HeadroomFactor) <= budgetBytes)
            {
                best = entry;
            }
        }

        return best ?? models[0];
    }
}
