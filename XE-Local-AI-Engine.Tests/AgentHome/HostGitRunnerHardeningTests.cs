namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.ComponentModel;
using System.Diagnostics;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The host-side runner carries <c>AgentHomeGitHardening.Environment</c>, not just the <c>-c</c> pins.
/// </summary>
/// <remarks>
///     This git runs against the OPERATOR's checkout, where global and system configuration are real, and
///     <c>git apply</c> consults the target's in-tree <c>.gitattributes</c> — which can NAME arbitrary
///     <c>filter.&lt;driver&gt;.clean</c> and <c>diff.&lt;name&gt;.textconv</c> programs, a class the <c>-c</c> pins
///     cannot close. Proved by planting a marker in a global configuration file; <c>[NotInParallel]</c> because the
///     probe must set <c>GIT_CONFIG_GLOBAL</c> on this process for the child to inherit it.
/// </remarks>
[TUnit.Core.Category(TestCategories.Integration)]
[NotInParallel]
public sealed class HostGitRunnerHardeningTests : IDisposable
{
    private const string MarkerKey = "agenthomehardeningprobe.marker";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-host-git-hardening-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
    }

    [Test]
    public async Task RunAsync_DoesNotReadTheGlobalGitConfiguration()
    {
        Directory.CreateDirectory(_root);
        var globalConfig = Path.Combine(_root, "planted.gitconfig");
        await File.WriteAllTextAsync(globalConfig, "[agenthomehardeningprobe]\n\tmarker = planted\n");

        var previous = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", globalConfig);
        try
        {
            var runner = new HostGitRunner(timeoutSeconds: 30);

            // Non-vacuity: git must actually be able to read the planted file when nothing removes it from its
            // search. Without this, an unreadable file would make the assertion below pass for the wrong reason.
            var (exitCode, standardOutput, standardError) = await RawGitAsync(globalConfig);
            AssertEx.Equal(expected: 0, exitCode,
                $"git could not read the planted global configuration at all, so this probe proves nothing. stderr: {standardError}");
            AssertEx.Contains(standardOutput, "planted");

            var hardened = await runner.RunAsync(_root, AgentHomeGit.Arguments("config", "--get", MarkerKey), CancellationToken.None);

            AssertEx.False(hardened.ExitCode == 0 && hardened.StandardOutput.Contains("planted", StringComparison.Ordinal),
                "the runner read a value out of the GLOBAL git configuration. GIT_CONFIG_GLOBAL is not reaching the "
                + "process, so a filter or textconv driver defined there would execute on a host apply.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previous);
        }
    }

    /// <summary>
    ///     A caller that cancels mid-command leaves no git behind.
    /// </summary>
    /// <remarks>
    ///     The kill used to sit on the internal-timeout branch only, so a caller's token firing mid-apply returned
    ///     with the child still running while <c>NodePatchApplyService</c> released the node-wide apply gate — a
    ///     second apply into a tree an unsupervised git was still mutating. Gated by the test, not a sleep: the
    ///     patch argument is a FIFO, so git's open-for-read and the test's open-for-write rendezvous before the
    ///     cancel, and afterwards a write into it must fail, which is what a departed reader looks like.
    /// </remarks>
    [Test]
    public async Task RunAsync_WhenTheCallerCancels_KillsTheChildBeforeItReturns()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("The rendezvous uses a POSIX FIFO, which Windows has no equivalent of; the guard itself is platform-neutral.");
        }

        Directory.CreateDirectory(_root);
        var fifo = Path.Combine(_root, "blocking.patch");
        if (!await TryMakeFifoAsync(fifo))
        {
            Skip.Test("This host has no usable mkfifo, so the rendezvous this test depends on cannot be built.");
        }

        var runner = new HostGitRunner(timeoutSeconds: 120);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(_root, AgentHomeGit.Arguments("apply", "--check", fifo), cts.Token);

        // Blocks until git has opened the read end. When it returns, git is running and waiting on this pipe.
        var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write);
        await using (writer)
        {
            await cts.CancelAsync();

            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await run,
                "a caller that cancels is owed cancellation — but only once the child is gone.");

            // The proof. No reader left on the pipe means the process this runner started is no longer there.
            var stillReading = true;
            try
            {
                await writer.WriteAsync(new byte[]
                {
                    0x0A
                });
                await writer.FlushAsync();
            }
            catch (IOException)
            {
                stillReading = false;
            }

            AssertEx.False(stillReading,
                "the git process was still reading its input after RunAsync returned, so a cancelled apply outlives the "
                + "node-wide gate its caller released on the way out.");
        }
    }

    /// <summary>
    ///     Creates a FIFO with the system <c>mkfifo</c>. There is no .NET API for one, and this is a test fixture
    ///     rather than product code, so shelling out is the honest shape.
    /// </summary>
    private static async Task<bool> TryMakeFifoAsync(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "mkfifo",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(path);

        try
        {
            using var process = new Process
            {
                StartInfo = startInfo
            };
            _ = process.Start();
            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 && File.Exists(path);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     The same read WITHOUT the runner, so the planted file is proved readable before its absence is asserted.
    ///     A bare <c>Process</c> rather than a second runner: the runner is the thing under test.
    /// </summary>
    private async Task<(int ExitCode, string StandardOutput, string StandardError)> RawGitAsync(string globalConfig)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _root
        };
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add("--get");
        startInfo.ArgumentList.Add(MarkerKey);
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = globalConfig;

        using var process = new Process
        {
            StartInfo = startInfo
        };
        _ = process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
