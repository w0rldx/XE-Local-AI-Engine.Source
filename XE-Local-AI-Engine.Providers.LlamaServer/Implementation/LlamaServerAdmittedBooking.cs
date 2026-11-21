namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>The one check every post-admission substitution passes: the launched allocation stays within what admission booked.</summary>
internal static class LlamaServerAdmittedBooking
{
    /// <summary>
    ///     Whether <paramref name="candidate" /> fits <paramref name="admitted" />: no larger window (nor more chat slots at the same window), and
    ///     no more GPU or RAM bytes.
    /// </summary>
    public static bool Fits(ProcessContextAllocation candidate, ProcessContextAllocation admitted)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(admitted);
        var windowFits = candidate.ProcessContextTokens < admitted.ProcessContextTokens
                         || (candidate.ProcessContextTokens == admitted.ProcessContextTokens && candidate.ChatSlots <= admitted.ChatSlots);
        return windowFits
               && candidate.Footprint.GpuBytes <= admitted.Footprint.GpuBytes
               && candidate.Footprint.RamBytes <= admitted.Footprint.RamBytes;
    }
}
