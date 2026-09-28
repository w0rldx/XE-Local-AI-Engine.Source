namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The receipt reap against real orphans: two <c>sleep</c> processes of the SAME binary, reparented away from this process the
///     way a hard host kill leaves a server, outside any managed root. Only the one this "node" holds a receipt for may die.
/// </summary>
/// <remarks>
///     This is the shared-binary cross-kill the receipt exists to prevent: path containment cannot tell the two apart, pid +
///     <c>/proc</c> start time + executable realpath can. The real <see cref="OsStaleProcessScanner" /> reads <c>/proc</c> and signals.
/// </remarks>
[Category(TestCategories.Integration)]
[RunOn(OS.Linux)]
[SupportedOSPlatform("linux")]
public sealed class SpawnReceiptReapLinuxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"xe-receipt-reap-{Guid.NewGuid():N}");
    private readonly List<int> _started = [];

    public void Dispose()
    {
        foreach (var pid in _started)
        {
            TryKill(pid);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task OrphanWithOurReceipt_IsReaped_SameBinaryOrphanWithoutOne_IsNot()
    {
        var sleep = SleepPath();
        var ours = await StartOrphanAsync(sleep);
        var theirs = await StartOrphanAsync(sleep);
        var store = new ProcessSpawnReceiptStore(_root, "sleep", NullLogger.Instance);
        using var handle = new PidOnlyHandle(ours);

        // The tracked handle is deliberately never disposed: that would remove the receipt, as a clean teardown does.
        _ = await store.TrackAsync(handle, sleep, "ours", CancellationToken.None);
        AssertEx.Equal(expected: 1, ReceiptCount());

        // A binaries root that holds neither: only the receipt can claim anything.
        var reaper = new StaleProcessReaper(new OsStaleProcessScanner("sleep"),
            Path.Combine(_root, "managed-root"),
            "sleep",
            logExecutablePath: true,
            NullLogger<StaleProcessReaper>.Instance,
            store);
        await reaper.StartAsync(CancellationToken.None);

        await AssertEx.EventuallyAsync(() => !IsAlive(ours), TimeSpan.FromSeconds(5), "The receipted orphan survived the reap.");
        AssertEx.True(IsAlive(theirs), "A process of the same binary without this node's receipt must never be signalled.");
        AssertEx.Equal(expected: 0, ReceiptCount());
    }

    [Test]
    public async Task OrphanOfABinaryNamedUnlikeTheScannedProcess_IsReapedByItsReceipt_SameBinaryOrphanWithoutOne_IsNot()
    {
        // A bring-your-own server may carry any file name: the scanner looks for "sleep", the orphans run a renamed copy of it.
        var renamed = Path.Combine(_root, "byo", "xe-byo-sleep");
        Directory.CreateDirectory(Path.GetDirectoryName(renamed)!);
        File.Copy(SleepPath(), renamed);
        File.SetUnixFileMode(renamed, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var ours = await StartOrphanAsync(renamed);
        var theirs = await StartOrphanAsync(renamed);
        var store = new ProcessSpawnReceiptStore(_root, "sleep", NullLogger.Instance);
        using var handle = new PidOnlyHandle(ours);
        _ = await store.TrackAsync(handle, renamed, "ours", CancellationToken.None);
        AssertEx.Equal(expected: 1, ReceiptCount());

        var reaper = new StaleProcessReaper(new OsStaleProcessScanner("sleep"),
            Path.Combine(_root, "managed-root"),
            "sleep",
            logExecutablePath: true,
            NullLogger<StaleProcessReaper>.Instance,
            store);
        await reaper.StartAsync(CancellationToken.None);

        await AssertEx.EventuallyAsync(() => !IsAlive(ours), TimeSpan.FromSeconds(5), "The receipted orphan of a renamed binary survived the reap.");
        AssertEx.True(IsAlive(theirs), "A process of the same renamed binary without this node's receipt must never be signalled.");
        AssertEx.Equal(expected: 0, ReceiptCount());
    }

    [Test]
    public async Task KillIfSameProcess_WithAStaleStartTime_SignalsNothing()
    {
        var pid = await StartOrphanAsync(SleepPath());
        var scanner = new OsStaleProcessScanner("sleep");
        var stat = scanner.ReadStat(pid);
        AssertEx.True(stat.HasValue, "A live orphan's stat must be readable.");
        var startTicks = stat!.Value.StartTicks;

        AssertEx.False(scanner.KillIfSameProcess(pid, startTicks + 1), "A start-time mismatch is a recycled pid.");
        AssertEx.True(IsAlive(pid));

        AssertEx.True(scanner.KillIfSameProcess(pid, startTicks));
        await AssertEx.EventuallyAsync(() => !IsAlive(pid), TimeSpan.FromSeconds(5), "The exact process was not signalled.");
    }

    /// <summary>
    ///     A <c>sleep</c> started by a shell that exits at once, so the sleep is reparented away from this process: an orphan, as a
    ///     hard host kill leaves one. Returns its pid.
    /// </summary>
    private async Task<int> StartOrphanAsync(string sleep)
    {
        // The kernel's comm is the file name, cut to 15 bytes.
        var comm = Path.GetFileName(sleep);
        comm = comm[..Math.Min(comm.Length, 15)];
        var marker = Path.Combine(Path.GetTempPath(), $"xe-orphan-{Guid.NewGuid():N}.pid");
        var startInfo = new ProcessStartInfo
        {
            FileName = SetsidLocator.ResolveAbsolutePath(),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/bin/sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"'{sleep}' 600 & echo $! > '{marker}'");
        using (var shell = Process.Start(startInfo) ?? throw new InvalidOperationException("The shell did not start."))
        {
            await shell.WaitForExitAsync();
        }

        try
        {
            await AssertEx.EventuallyAsync(() => File.Exists(marker) && new FileInfo(marker).Length > 0, TimeSpan.FromSeconds(5), "The orphan pid was not written.");
            var pid = int.Parse((await File.ReadAllTextAsync(marker)).Trim(), CultureInfo.InvariantCulture);
            _started.Add(pid);

            // The forked shell has to have exec'd sleep before /proc names the binary the receipt records.
            await AssertEx.EventuallyAsync(() => string.Equals(ReadProc(pid, "comm")?.Trim(), comm, StringComparison.Ordinal),
                TimeSpan.FromSeconds(5), "The orphan never exec'd sleep.");
            AssertEx.NotEqual(Environment.ProcessId, new OsStaleProcessScanner("sleep").ReadStat(pid)?.ParentProcessId ?? 0, "The orphan must not be this process's child.");
            return pid;
        }
        finally
        {
            File.Delete(marker);
        }
    }

    private static string SleepPath() =>
        new[] { "/usr/bin/sleep", "/bin/sleep" }.First(File.Exists);

    private static string? ReadProc(int pid, string entry)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/{entry}");
        }
        catch (IOException)
        {
            return null;
        }
    }

    private int ReceiptCount()
    {
        var directory = Path.Combine(_root, "runtime", "sleep");
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Count() : 0;
    }

    // A killed orphan can linger as a zombie until init reaps it; that is dead.
    private static bool IsAlive(int pid) =>
        ReadProc(pid, "stat") is { } stat && !stat.Contains(") Z ", StringComparison.Ordinal);

    private static void TryKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Already gone.
        }
    }

    /// <summary>Carries only a pid: the receipt store needs nothing else, and disposing it must not touch the orphan.</summary>
    private sealed class PidOnlyHandle : IProcessTreeHandle
    {
        public PidOnlyHandle(int pid)
        {
            ProcessId = pid;
        }

        public int ProcessId { get; }

        public bool HasExited => false;

        public int? ExitCode => null;

        public string? StderrTail => null;

        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(false);

        public void TreeKill()
        {
        }

        public void Dispose()
        {
        }
    }
}
