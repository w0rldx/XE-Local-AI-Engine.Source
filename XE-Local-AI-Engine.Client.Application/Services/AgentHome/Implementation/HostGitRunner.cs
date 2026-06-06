namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.ComponentModel;
using System.Diagnostics;

/// <summary>
///     Outcome of a host <c>git</c> invocation: exit code plus captured stdout/stderr.
/// </summary>
internal sealed record HostGitResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
///     Runs host-side <c>git</c> commands for the host patch apply flow. Mirrors the only existing
///     <see cref="Process" /> use in this assembly (<c>CapabilityReportComposer</c>): a CA2000-clean <c>using var</c> process
///     with redirected stdout/stderr, <see cref="ProcessStartInfo.ArgumentList" /> (never a joined string, so paths with
///     spaces are safe), and a <see cref="System.Threading.Tasks.Task" />-based read + wait. The hardened <c>-c</c> flags
///     come from <see cref="AgentHomeGit" /> so a host global hook or <c>.gitattributes</c> cannot interfere with the
///     apply.
/// </summary>
internal sealed class HostGitRunner
{
    private readonly int _timeoutSeconds;

    public HostGitRunner(int timeoutSeconds)
    {
        _timeoutSeconds = timeoutSeconds;
    }

    /// <summary>
    ///     Runs <c>git</c> with the given hardened argument list in <paramref name="workingDirectory" /> and returns the
    ///     exit code and captured streams. A timeout, a missing executable, or a launch failure surfaces as a non-zero
    ///     exit with the failure on stderr rather than throwing past the caller.
    /// </summary>
    public async Task<HostGitResult> RunAsync(string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = AgentHomeGit.Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        using var process = new Process
        {
            StartInfo = startInfo
        };

        try
        {
            if (!process.Start())
            {
                return new HostGitResult(-1, string.Empty, "git could not be started.");
            }
        }
        catch (Win32Exception exception)
        {
            // git is not installed / not on PATH.
            return new HostGitResult(-1, string.Empty, exception.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The per-command timeout (not the caller) fired: kill the process and surface a non-zero result.
            TryKill(process);
            return new HostGitResult(-1, string.Empty, "git timed out.");
        }

        var standardOutput = await stdoutTask.ConfigureAwait(false);
        var standardError = await stderrTask.ConfigureAwait(false);
        return new HostGitResult(process.ExitCode, standardOutput, standardError);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
        catch (Win32Exception)
        {
            // Could not signal the process; nothing more to do.
        }
    }
}
