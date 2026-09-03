namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Runtime verification of the Linux process-group tree-kill path — the Linux branch a WSL2/Linux
///     environment CAN exercise, unlike the Windows Job Object path. Spawns a real shell that forks a child, then
///     tree-kills via the production launcher and asserts NO descendant survives (no orphan).
/// </summary>
/// <remarks>
///     The Windows Job Object branch is implemented to the <c>dotnet-pinvoke</c> standard but cannot run here — it is
///     flagged for operator verification on real Windows 11. This test guards the half that runs on Linux.
/// </remarks>
public sealed class LinuxProcessGroupTreeKillTests
{
    [Test]
    public async Task Launch_ThenTreeKill_KillsChildProcessGroup_NoOrphan()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return; // Linux-only runtime verification; the Windows path is operator-verified.
        }

        var launcher = new LlamaServerProcessLauncher(NullLogger<LlamaServerProcessLauncher>.Instance);

        // A shell that spawns a long-lived grandchild and writes its PID, then sleeps. setsid makes the shell a
        // group leader so kill(-pgid) must reap BOTH the shell and the grandchild.
        var markerFile = Path.Combine(Path.GetTempPath(), $"xe-pgid-test-{Guid.NewGuid():N}.pid");
        var script = $"sleep 600 & echo $! > '{markerFile}'; sleep 600";
        var spec = BuildShellSpec(script);

        var handle = launcher.Launch(spec);
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

    private static LlamaServerLaunchSpec BuildShellSpec(string script)
    {
        // Point the "executable" at /bin/sh with the script. The launcher prepends `setsid` on Linux, so this
        // exercises the real process-group containment + kill(-pgid) teardown.
        return new LlamaServerLaunchSpec("test-model", ModelRole.Chat, "/bin/sh", ["-c", script], Port: 0, Path.GetTempPath());
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
