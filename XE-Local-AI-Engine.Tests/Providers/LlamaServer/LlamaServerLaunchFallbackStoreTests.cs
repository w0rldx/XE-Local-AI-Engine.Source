namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="LlamaServerLaunchFallbackStore" /> keying: the optimized-config verdict is per (backend, KV-cache
///     type), so one type's readiness failure cannot disable the type that works — and a legacy backend-only entry
///     written before this keying is ignored and dropped from the file on the first read.
/// </summary>
public sealed class LlamaServerLaunchFallbackStoreTests
{
    private const string StateFileName = "llama-launch-fallback.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task LegacyVariantEntry_DisablesNothing_AndIsDroppedFromTheFile()
    {
        using var root = new TempCacheRoot();
        // The shape an older build wrote: a bare backend name, no KV type. It cannot say which config failed, and
        // reading it as "every type on this backend" made the node's KV-cache-type setting inert on such a host.
        await File.WriteAllTextAsync(root.StatePath, """{"disabledOptimizedVariants":["Cuda"]}""");
        using (var store = new LlamaServerLaunchFallbackStore(root.Path))
        {
            AssertEx.False(await store.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None));
            AssertEx.False(await store.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q4_0, CancellationToken.None));
        }

        var persisted = await ReadStateAsync(root);
        AssertEx.Equal(expected: 0, persisted.DisabledOptimizedVariants.Count);
        AssertEx.Equal(expected: 0, (persisted.DisabledOptimizedConfigs ?? []).Count);
    }

    [Test]
    public async Task LegacyVariantEntry_IsDropped_ButAPairOnTheSameBackendSurvives()
    {
        using var root = new TempCacheRoot();
        await File.WriteAllTextAsync(root.StatePath,
            """{"disabledOptimizedVariants":["Cuda"],"disabledOptimizedConfigs":["Cuda:q4_0"]}""");
        using (var store = new LlamaServerLaunchFallbackStore(root.Path))
        {
            AssertEx.True(await store.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q4_0, CancellationToken.None),
                "The keyed q4_0 verdict must survive the legacy drop.");
            AssertEx.False(await store.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None),
                "Only the legacy entry covered q8_0, so dropping it must re-enable q8_0.");
        }

        var persisted = await ReadStateAsync(root);
        AssertEx.Equal(expected: 0, persisted.DisabledOptimizedVariants.Count);
        AssertEx.Contains(persisted.DisabledOptimizedConfigs ?? [], "Cuda:q4_0");
    }

    [Test]
    public async Task Q4Failure_LeavesQ8Enabled()
    {
        using var root = new TempCacheRoot();
        using (var store = new LlamaServerLaunchFallbackStore(root.Path))
        {
            await store.DisableOptimizedConfigAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q4_0, CancellationToken.None);

            AssertEx.True(await store.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q4_0, CancellationToken.None));
            AssertEx.False(await store.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None),
                "A q4_0 readiness failure says nothing about q8_0 and must not disable it.");
            AssertEx.False(await store.IsOptimizedConfigDisabledAsync(GpuVariant.Vulkan, LlamaServerKvCacheTypes.Q4_0, CancellationToken.None));
        }

        // The verdict survives a restart, and it is written to the new pair list rather than the legacy one.
        var persisted = await ReadStateAsync(root);
        AssertEx.Contains(persisted.DisabledOptimizedConfigs ?? [], "Cuda:q4_0");
        AssertEx.Equal(expected: 0, persisted.DisabledOptimizedVariants.Count);

        using var reopened = new LlamaServerLaunchFallbackStore(root.Path);
        AssertEx.True(await reopened.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q4_0, CancellationToken.None));
        AssertEx.False(await reopened.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None));
    }

    [Test]
    public async Task UnwritableStateFile_IsToleratedOnTheReadPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TempCacheRoot();
        // The legacy drop writes on the read path, which is the spawn hot path. A read-only cache root must therefore
        // degrade to "nothing disabled" instead of faulting the launch.
        await File.WriteAllTextAsync(root.StatePath, """{"disabledOptimizedVariants":["Cuda"]}""");
        File.SetUnixFileMode(root.StatePath, UnixFileMode.UserRead);
        File.SetUnixFileMode(root.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var store = new LlamaServerLaunchFallbackStore(root.Path);

            AssertEx.False(await store.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None));
        }
        finally
        {
            // Restore write access so TempCacheRoot can delete the directory.
            File.SetUnixFileMode(root.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(root.StatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task<LlamaServerLaunchFallbackState> ReadStateAsync(TempCacheRoot root)
    {
        var persisted = JsonSerializer.Deserialize<LlamaServerLaunchFallbackState>(await File.ReadAllTextAsync(root.StatePath),
            SerializerOptions);
        AssertEx.NotNull(persisted);
        return persisted!;
    }

    private sealed class TempCacheRoot : IDisposable
    {
        public TempCacheRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string StatePath => System.IO.Path.Combine(Path, StateFileName);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
