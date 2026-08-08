namespace XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

/// <summary>
///     Tiny seam over free-disk-space measurement so the store's hard disk guard is unit-testable without touching a
///     real volume. Returns the bytes currently free on the volume hosting <paramref name="path" />.
/// </summary>
public interface IFreeSpaceProbe
{
    /// <summary>Returns the available free bytes on the volume that hosts <paramref name="path" />.</summary>
    long GetAvailableFreeBytes(string path);
}
