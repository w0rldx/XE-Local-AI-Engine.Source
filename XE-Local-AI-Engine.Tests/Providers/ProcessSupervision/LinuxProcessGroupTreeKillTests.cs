namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Runtime verification of the Linux process-group tree-kill path — the Linux branch a WSL2/Linux
///     environment CAN exercise, unlike the Windows Job Object path. Spawns a real shell under <c>setsid</c> that forks a
///     child, then tree-kills via the shared <see cref="LinuxProcessGroupHandle" /> and asserts NO descendant survives.
/// </summary>
/// <remarks>
///     The Windows Job Object branch is implemented to the <c>dotnet-pinvoke</c> standard but cannot run here — it is
///     flagged for operator verification on real Windows 11. This test guards the half that runs on Linux, for every
///     provider that launches through the shared handle.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class LinuxProcessGroupTreeKillTests
{
    // Linux-only runtime verification; the Windows path is operator-verified.
    [Test]
    [RunOn(OS.Linux)]
    [SupportedOSPlatform("linux")]
    public async Task Wrap_ThenTreeKill_KillsChildProcessGroup_NoOrphan()
    {
        // A shell that spawns a long-lived grandchild and writes its PID, then sleeps. setsid makes the shell a
        // group leader so kill(-pgid) must reap BOTH the shell and the grandchild.
        var markerFile = Path.Combine(Path.GetTempPath(), $"xe-pgid-test-{Guid.NewGuid():N}.pid");
        var script = $"sleep 600 & echo $! > '{markerFile}'; sleep 600";

        var handle = LinuxProcessGroupHandle.Wrap(StartUnderSetsid(script));
        try
        {
            await AssertEx.EventuallyAsync(() => File.Exists(markerFile) && new FileInfo(markerFile).Length > 0,
                TimeSpan.FromSeconds(5), "Grandchild PID marker was not written.");

            var grandchildPid = int.Parse((await File.ReadAllTextAsync(markerFile)).Trim(), CultureInfo.InvariantCulture);
            AssertEx.True(IsProcessAlive(grandchildPid), "Grandchild should be alive before tree-kill.");

            handle.TreeKill();

            // The whole process group must be gone — verify the grandchild (the orphan-risk process) is reaped.
            await AssertEx.EventuallyAsync(() => !IsProcessAlive(grandchildPid),
                TimeSpan.FromSeconds(5), "Grandchild survived tree-kill — orphaned process group.");
        }
        finally
        {
            handle.Dispose();
            TryDelete(markerFile);
        }
    }

    /// <summary>Starts <c>/bin/sh -c script</c> under <c>setsid</c>, exactly as the runtime launchers start their servers.</summary>
    [SupportedOSPlatform("linux")]
    private static Process StartUnderSetsid(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = SetsidLocator.ResolveAbsolutePath(),
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/bin/sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);

        return Process.Start(startInfo) ?? throw new InvalidOperationException("The shell did not start.");
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var _ = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false; // No such process.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
