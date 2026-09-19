namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Finds the CUDA compiler when it is installed where NVIDIA's Linux installer puts it — <c>/usr/local/cuda/bin</c>
///     — but that directory is not on the host process PATH, which is the default on a box where CUDA was installed
///     from the runfile or the distro package without a login-shell profile edit.
///     <para>
///         This exists because the source-build prerequisite probes were stricter than the build they gate: a probe
///         spawns <c>nvcc</c> by bare name, which .NET resolves against the PARENT process PATH only, while the build
///         runs CMake, whose <c>FindCUDAToolkit</c> falls back to <c>/usr/local/cuda</c> on its own. The checklist
///         therefore said "CUDA compiler Missing" on a host that builds fine. Providers use
///         <see cref="FindNvccOutsidePath" /> for both halves: the probe spawns the returned absolute path, and the
///         build child gets <c>CUDACXX</c> set to it so CMake picks the same toolkit the checklist was answered from.
///     </para>
///     <para>
///         Nothing host-derived is ever executed: the file name is the fixed literal <c>nvcc</c>, the directory is
///         always <c>{root}/bin</c> of a fully qualified root, and the roots are the two variables NVIDIA's own
///         installers set plus one absolute constant. No shell is involved anywhere.
///     </para>
///     <para>
///         Linux-only today, by the file name (<c>nvcc</c>, no <c>.exe</c>) and by the conventional root: every call
///         site sits behind an <c>OperatingSystem.IsLinux()</c> gate in the three prerequisite probes, so the gap is
///         unreachable rather than a live bug. Extend both before reusing this from a Windows source-build lane —
///         nothing in this project's type system stops a future caller from assuming it is cross-platform.
///     </para>
/// </summary>
public static class CudaToolkitLocator
{
    private const string NvccFileName = "nvcc";

    /// <summary>The conventional CUDA install root on Linux; the last candidate, tried only when neither variable is set.</summary>
    private const string DefaultLinuxCudaRoot = "/usr/local/cuda";

    /// <summary>The variables NVIDIA's installers and the CUDA module files export, in the order CMake itself prefers them.</summary>
    private static readonly string[] RootVariables = ["CUDA_HOME", "CUDA_PATH"];

    /// <summary>
    ///     The absolute <c>nvcc</c> a caller should use instead of the bare name, or <c>null</c> when PATH already
    ///     resolves one (nothing to correct) or no conventional install exists (nothing to offer). Returning
    ///     <c>null</c> for the PATH case keeps the behaviour of every already-working host byte-identical.
    /// </summary>
    public static string? FindNvccOutsidePath()
    {
        if (ResolveOnPath() is not null)
        {
            return null;
        }

        // An operator who exports CUDA_HOME/CUDA_PATH has named the toolkit to use, so a set variable is authoritative:
        // the conventional root is consulted only when neither is set. Without that rule a broken explicit root would
        // be silently overruled by whatever happens to sit in /usr/local/cuda. This is deliberately STRICTER than
        // CMake, which would still fall back to /usr/local/cuda given a hint variable that resolves to nothing: the
        // disagreement can only make the checklist refuse a build CMake might have managed, never the reverse.
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

    // The same lookup .NET's own Process.Start does for a file name with no directory separator: the PARENT process
    // PATH, entry by entry. Reimplemented rather than inferred from a spawn, because "did it run" and "is it there"
    // must not be conflated here — the caller still spawns the tool to prove it actually works.
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
