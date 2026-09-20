namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using XE_Local_AI_Engine.Client.Common;

/// <summary>
///     Runs host-side <c>git</c> commands for the host patch apply flow.
/// </summary>
/// <remarks>
///     It mirrors this assembly's only other <see cref="Process" /> use, <c>CapabilityReportComposer</c>: a CA2000-clean
///     <c>using var</c> process with redirected stdout/stderr, <see cref="ProcessStartInfo.ArgumentList" /> rather than a
///     joined string so paths with spaces are safe, and a <see cref="System.Threading.Tasks.Task" />-based read plus wait.
///     The hardened <c>-c</c> flags come from <see cref="AgentHomeGit" />, so a host global hook or <c>.gitattributes</c>
///     cannot interfere with the apply.
/// </remarks>
internal sealed class HostGitRunner
{
    private readonly int _timeoutSeconds;

    public HostGitRunner(int timeoutSeconds)
    {
        _timeoutSeconds = timeoutSeconds;
    }

    /// <summary>
    ///     Runs <c>git</c> with the given hardened argument list in <paramref name="workingDirectory" /> and returns
    ///     the exit code and captured streams.
    /// </summary>
    /// <remarks>
    ///     A timeout, a missing executable or a launch failure surfaces as a non-zero exit with the failure on stderr, never a throw
    ///     past the caller. <paramref name="standardInput" /> feeds a command that reads <c>-</c> — <c>git apply</c> is the one that
    ///     does — so patch bytes go to git directly instead of a file first, one fewer copy of them on disk. When the input is
    ///     model-influenced, pass the bounds too: git echoes parts of a patch back on failure, and an unbounded read of that is the
    ///     caller's memory in a hostile patch's hands. Exceeding a bound is answered as a non-zero exit, like every other failure.
    /// </remarks>
    public async Task<HostGitResult> RunAsync(string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte>? standardInput = null,
        int? maxStandardOutputBytes = null,
        int? maxStandardErrorBytes = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

#pragma warning disable S4036 // git deliberately resolves via PATH: its install location varies per OS/distro (and per-user on Windows), invocations pin core.hooksPath and a sandboxed working directory, and a missing/hijacked binary surfaces as a captured non-zero exit — never an escalation.
        var startInfo = new ProcessStartInfo
        {
            FileName = AgentHomeGit.Executable,
#pragma warning restore S4036
            RedirectStandardInput = standardInput is not null,
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
                return new HostGitResult { ExitCode = -1, StandardOutput = string.Empty, StandardError = "git could not be started." };
            }
        }
        catch (Win32Exception exception)
        {
            // git is not installed / not on PATH.
            return new HostGitResult { ExitCode = -1, StandardOutput = string.Empty, StandardError = exception.Message };
        }

        // The drains start BEFORE stdin is written, and that order is load-bearing on a large patch: both pipes are OS
        // buffers of a few dozen kilobytes, so writing first deadlocks git against this method until the timeout.
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, maxStandardOutputBytes, timeoutCts.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, maxStandardErrorBytes, timeoutCts.Token);

        string? inputFailure = null;
        if (standardInput is { } input)
        {
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(input, timeoutCts.Token);
            }
            catch (IOException exception)
            {
                // git exited before reading the patch, so the write hit a broken pipe. Kept as a RESULT, because every
                // failure here comes back as a non-zero exit; the wait still runs, and this speaks up only if git did not.
                inputFailure = exception.Message;
            }

            // Outside the catch, so a failed write still closes: a git waiting for EOF on a pipe nobody closed stalls until
            // the timeout kill. Closing an already-broken pipe re-throws the same failure on the flush, nothing new.
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Already reported by the write above, or nothing was written at all.
            }
        }

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The per-command timeout (not the caller) fired: kill the process and surface a non-zero result.
            ProcessTermination.TryKill(process);
            return new HostGitResult { ExitCode = -1, StandardOutput = string.Empty, StandardError = "git timed out." };
        }

        var standardOutput = await stdoutTask;
        var standardError = await stderrTask;
        if (standardOutput.Truncated || standardError.Truncated)
        {
            return new HostGitResult { ExitCode = -1, StandardOutput = standardOutput.Text, StandardError = "git produced more output than its configured bound." };
        }

        // A git that stopped reading and then exited zero did not see the whole input, whatever its exit code claims.
        // One that exited non-zero has already said why, in its own words, which are better than the pipe's.
        return process.ExitCode == 0 && inputFailure is not null
            ? new HostGitResult { ExitCode = -1, StandardOutput = standardOutput.Text, StandardError = $"git stopped reading its input: {inputFailure}" }
            : new HostGitResult { ExitCode = process.ExitCode, StandardOutput = standardOutput.Text, StandardError = standardError.Text };
    }

    /// <summary>
    ///     Reads a stream to its end, or to <paramref name="maxBytes" /> — after which it keeps draining, so the
    ///     process can exit, and keeps nothing.
    /// </summary>
    /// <remarks>
    ///     Mirrors the trusted apply port's bounded reads. The cap is counted in chars, which for UTF-8 is never more
    ///     than the bytes it stands for.
    /// </remarks>
    private static async Task<BoundedRead> ReadBoundedAsync(StreamReader reader, int? maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes is not { } cap)
        {
            return new BoundedRead(await reader.ReadToEndAsync(cancellationToken), Truncated: false);
        }

        var buffer = new char[8192];
        var text = new StringBuilder();
        var truncated = false;
        while (await reader.ReadAsync(buffer, cancellationToken) is var read && read > 0)
        {
            if (truncated || text.Length + read > cap)
            {
                truncated = true;
                continue;
            }

            _ = text.Append(buffer, 0, read);
        }

        return new BoundedRead(text.ToString(), truncated);
    }

    private readonly record struct BoundedRead(string Text, bool Truncated);
}
