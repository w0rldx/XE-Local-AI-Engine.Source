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
///     The hardened <c>-c</c> flags from <see cref="AgentHomeGit" /> and the configuration cut from
///     <see cref="AgentHomeGitHardening.Environment" /> keep a host global hook or <c>.gitattributes</c> out of the apply.
/// </remarks>
internal sealed class HostGitRunner
{
    /// <summary>
    ///     How long a killed child is given to actually exit before this runner reports that it could not confirm it
    ///     had. A terminated process is reaped in milliseconds; this bound exists so an unreapable one is a reported
    ///     failure rather than a hang.
    /// </summary>
    private static readonly TimeSpan ReapTimeout = TimeSpan.FromSeconds(10);

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
        int? maxStandardErrorBytes = null,
        IReadOnlyDictionary<string, string>? environment = null)
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

        // This git runs against the OPERATOR's checkout, where global and system configuration is reachable and `git
        // apply` reads .gitattributes drivers; their names are arbitrary, so the files leave git's search entirely.
        foreach (var (key, value) in AgentHomeGitHardening.Environment)
        {
            startInfo.Environment[key] = value;
        }

        // Per-call additions, which never replace the hardening above: only a key it does not set may be passed.
        foreach (var (key, value) in environment ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (AgentHomeGitHardening.Environment.ContainsKey(key))
            {
                throw new ArgumentException($"'{key}' is part of the hardened git environment and cannot be overridden.", nameof(environment));
            }

            startInfo.Environment[key] = value;
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
                return new HostGitResult
                {
                    ExitCode = -1,
                    StandardOutput = string.Empty,
                    StandardError = "git could not be started."
                };
            }
        }
        catch (Win32Exception exception)
        {
            // git is not installed / not on PATH.
            return new HostGitResult
            {
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = exception.Message
            };
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
        catch (OperationCanceledException)
        {
            // Kill on EITHER token: a caller cancellation that propagated with the git still running would release
            // the node-wide apply gate over a tree that process keeps mutating. When this returns, the child is gone.
            ProcessTermination.TryKill(process);
            var reaped = await TryWaitForExitAsync(process);

            if (cancellationToken.IsCancellationRequested)
            {
                // The caller cancelled, so cancellation is what it is owed — but only once the child is gone, which
                // is the whole point of waiting here rather than rethrowing straight away.
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new HostGitResult
            {
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = reaped ? "git timed out." : "git timed out and did not exit after being terminated."
            };
        }

        var standardOutput = await stdoutTask;
        var standardError = await stderrTask;
        if (standardOutput.Truncated || standardError.Truncated)
        {
            return new HostGitResult
            {
                ExitCode = -1,
                StandardOutput = standardOutput.Text,
                StandardError = "git produced more output than its configured bound."
            };
        }

        // A git that stopped reading and then exited zero did not see the whole input, whatever its exit code claims.
        // One that exited non-zero has already said why, in its own words, which are better than the pipe's.
        return process.ExitCode == 0 && inputFailure is not null
            ? new HostGitResult
            {
                ExitCode = -1,
                StandardOutput = standardOutput.Text,
                StandardError = $"git stopped reading its input: {inputFailure}"
            }
            : new HostGitResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = standardOutput.Text,
                StandardError = standardError.Text
            };
    }

    /// <summary>
    ///     Waits for a just-killed process to exit, on a token of its OWN: the cancellation that caused the kill must
    ///     not cut this wait short. <see langword="false" /> means the child may still be running.
    /// </summary>
    private static async Task<bool> TryWaitForExitAsync(Process process)
    {
        using var reapCts = new CancellationTokenSource(ReapTimeout);
        try
        {
            await process.WaitForExitAsync(reapCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
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
