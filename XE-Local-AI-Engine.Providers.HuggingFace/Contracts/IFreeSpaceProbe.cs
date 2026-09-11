namespace XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

/// <summary>
///     Tiny seam over free-disk-space measurement so the store's hard disk guard is unit-testable without touching a
///     real volume. Returns the bytes currently free on the volume hosting <paramref name="path" />.
/// </summary>
public interface IFreeSpaceProbe
{
    /// <summary>
    ///     Returns the available free bytes on the volume that hosts <paramref name="path" />. The path need not
    ///     exist yet; the closest existing directory at or above it names the volume.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Nothing exists at or above <paramref name="path" />, so there is no volume to measure. Every caller has to
    ///     decide what an unmeasurable path means for its guard — it is not a zero-byte answer.
    /// </exception>
    long GetAvailableFreeBytes(string path);
}
