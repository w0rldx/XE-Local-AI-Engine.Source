namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>Limits shared by the managed llama.cpp, stable-diffusion.cpp and whisper.cpp source builds.</summary>
public static class SourceBuildPolicy
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
}
