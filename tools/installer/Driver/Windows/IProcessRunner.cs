namespace XE_Local_AI_Engine.Installer.Driver.Windows;

/// <summary>
///     Thin seam over external process invocation (<c>wsl.exe</c>, <c>powershell.exe</c>). Cross-platform
///     BCL only, so the whole installer project compiles on the Linux CI box; the
///     <see cref="WindowsInstallerDriver" /> is only instantiated at runtime under
///     <see cref="OperatingSystem.IsWindows" />.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken = default);
}

/// <summary>Exit code plus captured streams from a single external process invocation.</summary>
public sealed record ProcessRunResult
{
    public required int ExitCode { get; init; }

    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }
}
