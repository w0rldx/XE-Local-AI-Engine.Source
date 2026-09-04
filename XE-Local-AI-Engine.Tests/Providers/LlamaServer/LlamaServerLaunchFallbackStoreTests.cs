namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="LlamaServerLaunchFallbackStore" /> keying: the optimized-config verdict is per (backend, KV-cache
///     type), so one type's readiness failure cannot disable the type that works — while a legacy backend-only entry
///     written before this keying still disables everything on its backend, preserving the pre-upgrade behaviour.
/// </summary>
public sealed class LlamaServerLaunchFallbackStoreTests
{
    private const string StateFileName = "llama-launch-fallback.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task LegacyVariantEntry_DisablesEveryKvType()
    {
        using var root = new TempCacheRoot();
        // The shape an older build wrote: a bare backend name, no KV type. It was recorded when one verdict covered
        // every type, so it must keep covering every type — reading it narrowly would silently re-enable a config this
        // host already proved cannot reach readiness.
        await File.WriteAllTextAsync(root.StatePath, """{"disabledOptimizedVariants":["Cuda"]}""");
        using var store = new LlamaServerLaunchFallbackStore(root.Path);

        AssertEx.True(await store.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None));
        AssertEx.True(await store.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q4_0, CancellationToken.None));
        AssertEx.False(await store.IsOptimizedConfigDisabledAsync(GpuVariant.Vulkan, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None),
            "A legacy entry must not leak onto another backend.");
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
        var persisted = JsonSerializer.Deserialize<LlamaServerLaunchFallbackState>(await File.ReadAllTextAsync(root.StatePath),
            SerializerOptions);
        AssertEx.NotNull(persisted);
        AssertEx.Contains(persisted!.DisabledOptimizedConfigs ?? [], "Cuda:q4_0");
        AssertEx.Equal(expected: 0, persisted.DisabledOptimizedVariants.Count);

        using var reopened = new LlamaServerLaunchFallbackStore(root.Path);
        AssertEx.True(await reopened.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q4_0, CancellationToken.None));
        AssertEx.False(await reopened.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None));
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
