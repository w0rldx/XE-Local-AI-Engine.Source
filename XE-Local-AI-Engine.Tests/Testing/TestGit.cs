namespace XE_Local_AI_Engine.Tests.Testing;

using System.Diagnostics;
using System.Text;
using XE_Local_AI_Engine.Client.Testing.Git;

/// <summary>Runs <c>git</c> for a test fixture, with the inherited git environment <see cref="TestGitEnvironment" /> drops.</summary>
internal static class TestGit
{
    /// <inheritdoc cref="RunAsync(string, CancellationToken, string[])" />
    public static Task<string> RunAsync(string workingDirectory, params string[] arguments) =>
        RunAsync(workingDirectory, CancellationToken.None, arguments);

    /// <summary>
    ///     Runs git in <paramref name="workingDirectory" />, fails the test with stderr in the message on a non-zero
    ///     exit, and returns stdout untrimmed.
    /// </summary>
    /// <remarks>
    ///     Both pipes drain concurrently: awaiting one to its end while git fills the other blocks both processes once
    ///     the output passes the pipe buffer. Git never prompts (no terminal prompt, stdin is a pipe nobody writes).
    ///     A run that outlives <see cref="TestBudgets.Contended" /> or its token stops waiting for git and both pipes,
    ///     and kills git's process tree if git still runs.
    /// </remarks>
    public static async Task<string> RunAsync(string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        TestGitEnvironment.Isolate(startInfo);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TestBudgets.Contended);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var standardError = process.StandardError.ReadToEndAsync(deadline.Token);
        string output;
        string error;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            // A helper git started (a hook's background job) can hold the inherited pipes open after git exits.
            output = await standardOutput;
            error = await standardError;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"git {string.Join(' ', arguments)} did not finish within {TestBudgets.Contended}.");
        }

        AssertEx.Equal(expected: 0, process.ExitCode, $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }
}
