namespace XE_Local_AI_Engine.Providers.Training.Contracts;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The stable item keys the UI and <see cref="ITrainingRuntimeService" /> both key on. <see cref="FreeDisk" /> is compared by ordinal equality in
///     <c>TrainingRuntimeService.InstallAsync</c> to distinguish an "out of space" refusal from a missing-toolchain one,
///     mirroring the source build's <c>free-disk</c> key.
/// </summary>
public static class TrainingRuntimePrerequisiteKeys
{
    public const string Platform = "platform";
    public const string FreeDisk = "free-disk";
    public const string NvidiaDriver = "nvidia-driver";
    public const string SystemMemory = "system-memory";
    public const string Lockfile = "lockfile";
    public const string CCompiler = "c-compiler";
}

/// <summary>
///     Reports whether this machine can provision the Python training runtime. Read-only: probing never mutates the
///     cache root, so the UI may call it freely before the operator commits to a multi-gigabyte install.
/// </summary>
public interface ITrainingRuntimePrerequisiteProbe
{
    Task<SourceBuildPrerequisiteReport> ProbeAsync(CancellationToken ct);
}
