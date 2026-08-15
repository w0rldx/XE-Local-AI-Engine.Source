namespace XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     One prerequisite and whether it is satisfied. <paramref name="Key" /> is a stable machine token the UI and
///     <see cref="ITrainingRuntimeService" /> both key on (see <see cref="TrainingRuntimePrerequisiteKeys" />);
///     <paramref name="Detail" /> is the operator-facing explanation and must stay path- and secret-free.
/// </summary>
public sealed record TrainingRuntimePrerequisiteItem(string Key, bool Satisfied, string Detail);

/// <summary>Per-item prerequisite report. <see cref="CanInstall" /> is false when any required item is unsatisfied.</summary>
public sealed record TrainingRuntimePrerequisiteReport(bool CanInstall, IReadOnlyList<TrainingRuntimePrerequisiteItem> Items);

/// <summary>
///     The stable item keys. <see cref="FreeDisk" /> is compared by ordinal equality in
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
}

/// <summary>
///     Reports whether this machine can provision the Python training runtime. Read-only: probing never mutates the
///     cache root, so the UI may call it freely before the operator commits to a multi-gigabyte install.
/// </summary>
public interface ITrainingRuntimePrerequisiteProbe
{
    Task<TrainingRuntimePrerequisiteReport> ProbeAsync(CancellationToken ct);
}
