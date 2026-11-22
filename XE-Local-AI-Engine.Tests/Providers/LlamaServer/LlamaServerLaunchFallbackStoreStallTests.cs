namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Diagnostics;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;
using Win32Exception = System.ComponentModel.Win32Exception;

/// <summary>
///     <see cref="LlamaServerLaunchFallbackStore" /> under a write stalled in disk I/O: the verdict is readable the moment
///     it is recorded, and a writer of another key publishes its own while the stalled write still holds the disk.
/// </summary>
/// <remarks>
///     The stall is a FIFO in place of the state file: the persist's merge read blocks in its open until the test opens
///     the write end, so the stall is held on a barrier, not on a timing guess.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class LlamaServerLaunchFallbackStoreStallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public LlamaServerLaunchFallbackStoreStallTests()
    {
        Directory.CreateDirectory(_root);
    }

    private string StatePath => Path.Combine(_root, "llama-launch-fallback.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task AStalledPersist_BlocksNeitherAReaderNorAnotherKeysPublication()
    {
        using var store = new LlamaServerLaunchFallbackStore(_root);
        AssertEx.False(await store.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None));
        if (!TryCreateFifo(StatePath))
        {
            Skip.Test("This host has no usable mkfifo, so the stalled write cannot be built.");
            return;
        }

        // The merge read opens the FIFO synchronously, so the stalled writer runs on its own task.
        var stalled = Task.Run(() => store.DisableOptimizedConfigAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None));
        var writeEnd = Task.Run(() => new FileStream(StatePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        try
        {
            // The write end opens only once the persist opened the read end: the writer is now inside its disk I/O.
            await AssertEx.CompletesAsync(writeEnd, TestBudgets.Contended, "The persist never reached its merge read.");

            var read = store.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None);
            await AssertEx.CompletesAsync(read, TestBudgets.Contended, "A reader waited on the stalled write.");
            AssertEx.True(await read, "The verdict is published before its write.");

            var other = store.DisableOptimizedConfigAsync(GpuVariant.Vulkan, LlamaServerKvCacheTypes.Q4_0, CancellationToken.None);
            var otherRead = store.IsOptimizedConfigDisabledAsync(GpuVariant.Vulkan, LlamaServerKvCacheTypes.Q4_0, CancellationToken.None);
            await AssertEx.CompletesAsync(otherRead, TestBudgets.Contended, "A reader waited on the stalled write.");
            AssertEx.True(await otherRead, "Another key's verdict publishes while the stalled write holds the disk.");
            AssertEx.False(stalled.IsCompleted, "The first write is still stalled.");
            AssertEx.False(other.IsCompleted, "The second write queues behind the stalled one.");

            await using (var stream = await writeEnd)
            {
                await stream.WriteAsync("""{"disabledOptimizedConfigs":["Cuda:q4_0"]}"""u8.ToArray());
            }

            await Task.WhenAll(stalled, other).WaitAsync(TestBudgets.Contended);
        }
        finally
        {
            // A failing run must not leave the writer parked on the FIFO forever.
            if (writeEnd.IsCompletedSuccessfully)
            {
                await (await writeEnd).DisposeAsync();
            }
        }

        using var reopened = new LlamaServerLaunchFallbackStore(_root);
        AssertEx.True(await reopened.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None));
        AssertEx.True(await reopened.IsOptimizedConfigDisabledAsync(GpuVariant.Vulkan, LlamaServerKvCacheTypes.Q4_0, CancellationToken.None));
        AssertEx.True(await reopened.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q4_0, CancellationToken.None),
            "The verdict a sibling wrote into the file is merged, not overwritten.");
    }

    /// <summary>Makes <paramref name="path" /> a FIFO, or reports that this host cannot.</summary>
    private static bool TryCreateFifo(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("mkfifo")
            {
                ArgumentList =
                {
                    path
                },
                RedirectStandardError = true
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(milliseconds: 10000);
            return process.HasExited && process.ExitCode == 0 && File.Exists(path);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
