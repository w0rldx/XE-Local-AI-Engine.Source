namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>Whether a Development sandbox's commands ran behind the host filesystem boundary, as its validation report records it.</summary>
internal static class DevelopmentFilesystemBoundary
{
    /// <summary>
    ///     <see langword="true" /> for a sandbox the bubblewrap chain serves, and for a container, which has the boundary by construction
    ///     rather than per request; <see langword="false" /> for a host child that can write outside its workspace.
    /// </summary>
    public static bool IsServed(SandboxHandle handle, SandboxProviderCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return handle.Isolation == SandboxIsolationMode.Filesystem
               || (capabilities.HasFlag(SandboxProviderCapabilities.SuppliesImageToolchain)
                   && capabilities.HasFlag(SandboxProviderCapabilities.SupportsHostFilesystemBoundary));
    }
}
