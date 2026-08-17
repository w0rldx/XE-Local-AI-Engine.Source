namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Production <see cref="IConvertScriptSourceFetcher" />: a depth-1 fetch of a single commit from the canonical
///     llama.cpp repository, run under the same scrubbed, credential-free git environment the in-app source build uses.
/// </summary>
/// <remarks>
///     A single-commit fetch (rather than a clone) is what makes pinning enforceable: the requested SHA is what git is
///     asked for, so a moved tag or hijacked branch has nothing to move. The checked-out HEAD is re-read and returned
///     so the caller verifies provenance itself rather than trusting the fetch to have done it.
/// </remarks>
public sealed class GitConvertScriptSourceFetcher : IConvertScriptSourceFetcher
{
    private const string Repository = LlamaCppSourceBuildRequestValidation.OfficialRepository;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ShortCommandTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task<string> FetchAsync(string destinationDirectory, string commitSha, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);

        var environment = BuildScrubbedGitEnvironment(destinationDirectory);
        IReadOnlyList<GitStep> steps =
        [
            new(["-C", destinationDirectory, "init", "--quiet"], ShortCommandTimeout),
            new(["-C", destinationDirectory, "remote", "add", "origin", Repository], ShortCommandTimeout),
            new(["-C", destinationDirectory, "fetch", "--depth", "1", "--no-tags", "origin", commitSha], FetchTimeout),
            new(["-C", destinationDirectory, "checkout", "--detach", commitSha], ShortCommandTimeout)
        ];

        foreach (var step in steps)
        {
            var result = await RunAsync(step.Args, environment, destinationDirectory, step.Timeout, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new LlamaRuntimeException("Fetching the pinned llama.cpp conversion scripts failed.");
            }
        }

        var (revExit, head) = await RunAsync(["-C", destinationDirectory, "rev-parse", "HEAD"],
            environment,
            destinationDirectory,
            ShortCommandTimeout,
            ct).ConfigureAwait(false);
        return revExit == 0
            ? head.Trim()
            : throw new LlamaRuntimeException("The fetched llama.cpp conversion scripts could not be verified.");
    }

    // Mirrors the source build's git hardening: no system/global config, no credential helper, no interactive or
    // askpass prompt, and an isolated HOME so nothing on the box can redirect the fetch.
    private static Dictionary<string, string> BuildScrubbedGitEnvironment(string isolatedHome)
    {
        var scrubbed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[] { "PATH", "LANG", "LC_ALL" })
        {
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value)
            {
                scrubbed[key] = value;
            }
        }

        scrubbed["HOME"] = isolatedHome;
        scrubbed["GIT_CONFIG_NOSYSTEM"] = "1";
        scrubbed["GIT_TERMINAL_PROMPT"] = "0";
        scrubbed["GIT_ASKPASS"] = "/bin/false";
        scrubbed["SSH_ASKPASS"] = "/bin/false";
        return scrubbed;
    }

    private static async Task<ProcessCaptureResult> RunAsync(IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct)
    {
#pragma warning disable S4036 // git deliberately resolves through the scrubbed PATH: its install location varies per OS/distro, the environment below strips every config/credential/askpass hook, and a missing or hijacked binary surfaces as a non-zero exit whose fetched commit then fails the caller's pin check.
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
#pragma warning restore S4036
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        try
        {
            if (!process.Start())
            {
                return new ProcessCaptureResult(ExitCode: -1, string.Empty);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new LlamaRuntimeException("git is required to provision the llama.cpp conversion scripts.", exception);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            return new ProcessCaptureResult(process.ExitCode, await stdout.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new LlamaRuntimeException("Fetching the pinned llama.cpp conversion scripts exceeded its time limit.");
        }
        finally
        {
            ProcessCaptureRunner.TryKill(process);
        }
    }

    /// <summary>One git invocation of the pinned fetch sequence: its argument vector and the timeout it may take.</summary>
    private sealed record GitStep(IReadOnlyList<string> Args, TimeSpan Timeout);
}
