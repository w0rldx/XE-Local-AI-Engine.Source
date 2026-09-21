namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Finds the CUDA compiler when NVIDIA's Linux installer put it in <c>/usr/local/cuda/bin</c> but that directory is
///     not on the host process PATH, the default after a runfile or distro-package install with no profile edit.
/// </summary>
/// <remarks>
///     Nothing host-derived is ever executed: the file name is the fixed literal <c>nvcc</c>, the directory is always
///     <c>{root}/bin</c> of a fully qualified root, and the roots are the two variables NVIDIA's installers set plus one
///     absolute constant. No shell is involved anywhere. Linux-only today, by the file name (no <c>.exe</c>) and the
///     conventional root: every call site sits behind an <c>OperatingSystem.IsLinux()</c> gate in the three prerequisite
///     probes, so the gap is unreachable, not a live bug. Extend both before reusing this from a Windows lane.
/// </remarks>
public static class CudaToolkitLocator
{
    private const string NvccFileName = "nvcc";

    /// <summary>The conventional CUDA install root on Linux; the last candidate, tried only when neither variable is set.</summary>
    private const string DefaultLinuxCudaRoot = "/usr/local/cuda";

    /// <summary>The variables NVIDIA's installers and the CUDA module files export, in the order CMake itself prefers them.</summary>
    private static readonly string[] RootVariables = ["CUDA_HOME", "CUDA_PATH"];

    /// <summary>
    ///     The absolute <c>nvcc</c> a caller should use instead of the bare name, or <c>null</c> when PATH already
    ///     resolves one (nothing to correct) or no conventional install exists (nothing to offer).
    /// </summary>
    /// <remarks>
    ///     Returning <c>null</c> for the PATH case keeps the behaviour of every already-working host byte-identical. Both halves of a
    ///     source build go through it: the prerequisite probe spawns the returned absolute path, and the build child gets <c>CUDACXX</c>
    ///     set to it so CMake picks the same toolkit the checklist was answered from. Without it the probe is stricter than the build it
    ///     gates — a probe spawning <c>nvcc</c> by bare name resolves only against the PARENT process PATH, while CMake's
    ///     <c>FindCUDAToolkit</c> falls back to <c>/usr/local/cuda</c>, so the checklist reports a missing compiler on a host that builds.
    /// </remarks>
    public static string? FindNvccOutsidePath()
    {
        if (ResolveOnPath() is not null)
        {
            return null;
        }

        // A set CUDA_HOME/CUDA_PATH names the toolkit, so the conventional root applies only when neither is set; else a broken explicit root is overruled by whatever sits in /usr/local/cuda.
        // Deliberately STRICTER than CMake, which falls back to /usr/local/cuda even for a hint resolving to nothing: the checklist can then only refuse a build CMake could manage, never the reverse.
        var configured = false;
        foreach (var variable in RootVariables)
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            configured = true;
            if (NvccUnder(root) is { } fromVariable)
            {
                return fromVariable;
            }
        }

        return configured ? null : NvccUnder(DefaultLinuxCudaRoot);
    }

    // The same lookup .NET's own Process.Start does for a file name with no directory separator: the PARENT process PATH, entry by entry. Reimplemented rather
    // than inferred from a spawn, because "did it run" and "is it there" must not be conflated here — the caller still spawns the tool to prove it actually works.
    private static string? ResolveOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Candidate(directory, NvccFileName) is { } candidate)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? NvccUnder(string? root)
    {
        return string.IsNullOrWhiteSpace(root) ? null : Candidate(Path.Combine(root, "bin"), NvccFileName);
    }

    // A candidate is taken only when the directory is fully qualified and the file is really there: a relative PATH
    // entry or a relative CUDA_HOME would otherwise make the resolved program depend on the working directory.
    private static string? Candidate(string directory, string fileName)
    {
        try
        {
            if (!Path.IsPathFullyQualified(directory))
            {
                return null;
            }

            var candidate = Path.Combine(directory, fileName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch (ArgumentException)
        {
            // A PATH entry or root with characters the platform rejects is simply not a candidate.
            return null;
        }
    }
}
