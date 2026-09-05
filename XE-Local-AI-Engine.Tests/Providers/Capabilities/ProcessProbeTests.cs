namespace XE_Local_AI_Engine.Tests.Providers.Capabilities;

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Capabilities.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     <see cref="ProcessProbe" /> tests exercising the REAL process-spawn seam: a normal exit returns its code
///     + stdout; a probe that overruns its wall-clock deadline is killed (process tree — a descendant never survives to
///     complete its work) and returns a typed <c>TimedOut</c> result instead of hanging; caller cancellation tree-kills
///     and surfaces cancellation; a missing tool degrades to <see langword="null" />. The POSIX-shell cases are gated to
///     Linux (the box + CI); the missing-tool case is cross-platform.
/// </summary>
public sealed class ProcessProbeTests
{
    private static ProcessProbe CreateProbe()
    {
        return new ProcessProbe(NullLogger<ProcessProbe>.Instance);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task ProcessProbe_NormalExit_ReturnsExitCodeAndStdout()
    {
        var probe = CreateProbe();

        var result = await probe.RunAsync("sh", ["-c", "printf 'hello-probe'"], TimeSpan.FromSeconds(10), CancellationToken.None);

        var value = AssertEx.NotNull(result);
        AssertEx.False(value.TimedOut);
        AssertEx.Equal(expected: 0, value.ExitCode);
        AssertEx.True(value.StandardOutput.Contains("hello-probe", StringComparison.Ordinal), "stdout must be captured.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task ProcessProbe_Timeout_ReturnsTimedOut_PromptlyWithoutWaitingForTheProcess()
    {
        var probe = CreateProbe();
        var stopwatch = Stopwatch.StartNew();

        // The process would run for 30s; the 500ms deadline must fire long before that and return a typed timeout.
        var result = await probe.RunAsync("sh", ["-c", "sleep 30"], TimeSpan.FromMilliseconds(500), CancellationToken.None);
        stopwatch.Stop();

        var value = AssertEx.NotNull(result);
        AssertEx.True(value.TimedOut, "an overrun probe must report TimedOut.");
        AssertEx.True(value.ExitCode != 0);
        AssertEx.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"the bounded probe must return near its deadline, not after the process exits (elapsed {stopwatch.Elapsed}).");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task ProcessProbe_Timeout_KillsTheProcessTree_DescendantNeverCompletes()
    {
        var probe = CreateProbe();
        var marker = Path.Combine(Path.GetTempPath(), $"xe-probe-tree-{Guid.NewGuid():N}.marker");

        try
        {
            // The shell sleeps, THEN touches the marker. If the process tree is truly killed at the 300ms deadline, the
            // descendant sleep is reaped and the marker is never written; a surviving orphan would create it ~2s later.
            var result = await probe.RunAsync("sh", ["-c", $"sleep 2; touch '{marker}'"], TimeSpan.FromMilliseconds(300), CancellationToken.None);

            var value = AssertEx.NotNull(result);
            AssertEx.True(value.TimedOut);

            // Wait well past the descendant's 2s sleep; the marker must remain absent because the tree was killed.
            await Task.Delay(TimeSpan.FromSeconds(3.5), CancellationToken.None);
            AssertEx.False(File.Exists(marker), "the process tree must be killed on timeout so the descendant never runs.");
        }
        finally
        {
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task ProcessProbe_CallerCancellation_ThrowsOperationCanceled()
    {
        var probe = CreateProbe();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        // Caller cancellation (distinct from the internal deadline) tree-kills and surfaces cancellation to the caller.
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => probe.RunAsync("sh", ["-c", "sleep 30"], TimeSpan.FromSeconds(30), cts.Token));
    }

    [Test]
    public async Task ProcessProbe_MissingTool_ReturnsNull_NeverThrows()
    {
        var probe = CreateProbe();

        var result = await probe.RunAsync("xe-nonexistent-probe-tool-9f3c", ["--version"], TimeSpan.FromSeconds(5), CancellationToken.None);

        AssertEx.Null(result);
    }
}
