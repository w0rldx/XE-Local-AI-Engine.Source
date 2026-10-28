namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Text.RegularExpressions;

/// <summary>Limits shared by the managed llama.cpp, stable-diffusion.cpp and whisper.cpp source builds.</summary>
public static partial class SourceBuildPolicy
{
    /// <summary>Conservative free-disk floor for a CUDA source build.</summary>
    /// <remarks>
    ///     The clone plus the cmake and CUDA object tree of any one managed target comfortably fits in this; the check is
    ///     a disk-exhaustion guard, not a precise estimate. Re-checked immediately before the build starts, because the
    ///     probe value can go stale.
    /// </remarks>
    public const long RequiredFreeDiskBytes = 15L * 1024 * 1024 * 1024;

    // Fallback CUDA compute-architecture set when nvidia-smi's compute_cap cannot be read or validated. [secMED-1]
    public const string DefaultCudaArchitectures = "75;86;89;120";

    // -j cap: parallel build jobs are min(nproc, this) to bound peak memory/CPU during the build. [secMED-5]
    public const int MaxBuildJobs = 8;

    public static readonly TimeSpan BuildTimeout = TimeSpan.FromHours(2);
    public static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan ConfigureTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ShortCommandTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SmokeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     Maps nvidia-smi <c>compute_cap</c> lines to a sorted, de-duplicated CMake architecture list. Any line that is
    ///     not a strict <c>major.minor</c> capability of a supported architecture yields <see cref="DefaultCudaArchitectures" />.
    /// </summary>
    public static string ParseCudaArchitectures(string output)
    {
        var values = new SortedSet<int>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = ComputeCapabilityRegex().Match(line);
            if (!match.Success
                || !int.TryParse(match.Groups["major"].Value, out var major)
                || !int.TryParse(match.Groups["minor"].Value, out var minor))
            {
                return DefaultCudaArchitectures;
            }

            var architecture = (major * 10) + minor;
            if (!IsSupportedCudaArchitecture(architecture))
            {
                return DefaultCudaArchitectures;
            }

            _ = values.Add(architecture);
        }

        return values.Count == 0 ? DefaultCudaArchitectures : string.Join(';', values);
    }

    private static bool IsSupportedCudaArchitecture(int architecture)
    {
        return architecture is 50 or 52 or 53
            or 60 or 61 or 62
            or 70 or 72 or 75
            or 80 or 86 or 87 or 89
            or 90
            or 100 or 101 or 103
            or 110
            or 120 or 121;
    }

    [GeneratedRegex(@"^(?<major>[0-9]{1,2})\.(?<minor>[0-9])$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ComputeCapabilityRegex();
}
