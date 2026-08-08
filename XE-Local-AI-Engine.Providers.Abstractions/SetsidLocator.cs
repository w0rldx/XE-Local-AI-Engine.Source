namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Resolves the absolute path of the util-linux <c>setsid</c> binary. Process launches must not rely on a PATH
///     lookup for the wrapper command (Sonar S4036): the two candidates below cover usr-merged and legacy filesystem
///     layouts, and missing both means a broken Linux userland — fail loudly rather than spawn without a process group.
///     Shared by every provider that launches a managed runtime in its own process group (llama.cpp, stable-diffusion.cpp).
/// </summary>
public static class SetsidLocator
{
    private static readonly string[] CandidatePaths = ["/usr/bin/setsid", "/bin/setsid"];

    public static string ResolveAbsolutePath()
    {
        return Array.Find(CandidatePaths, File.Exists)
               ?? throw new InvalidOperationException("The 'setsid' utility was not found at /usr/bin/setsid or /bin/setsid; it is required to launch the local runtime in its own process group.");
    }
}
