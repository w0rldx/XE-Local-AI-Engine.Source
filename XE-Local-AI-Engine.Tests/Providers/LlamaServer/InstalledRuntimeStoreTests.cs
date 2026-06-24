namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The installed-runtime state file round-trips and tolerates an absent or corrupt file (first-run / drift) by
///     returning null rather than throwing.
/// </summary>
public sealed class InstalledRuntimeStoreTests
{
    [Test]
    public async Task RoundTrips()
    {
        using var cache = new TempDir();
        using var store = new InstalledRuntimeStore(cache.Path);
        var state = new InstalledRuntimeState("b9700", "llama-b9700-bin-ubuntu-x64.tar.gz", new string('e', 64),
            GpuVariant.Vulkan, DateTimeOffset.UtcNow);

        await store.WriteAsync(state, CancellationToken.None);
        var read = await store.ReadAsync(CancellationToken.None);

        var actual = AssertEx.NotNull(read);
        AssertEx.Equal(state.Tag, actual.Tag);
        AssertEx.Equal(state.Asset, actual.Asset);
        AssertEx.Equal(state.Sha256, actual.Sha256);
        AssertEx.Equal<GpuVariant>(state.Variant, actual.Variant);
    }

    [Test]
    public async Task ReadAsync_WhenFileAbsent_ReturnsNull()
    {
        using var cache = new TempDir();
        using var store = new InstalledRuntimeStore(cache.Path);

        var read = await store.ReadAsync(CancellationToken.None);

        AssertEx.Null(read);
    }

    [Test]
    public async Task ReadAsync_WhenFileCorrupt_ReturnsNullNoThrow()
    {
        using var cache = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(cache.Path, "installed-runtime.json"), "{ this is not valid json");
        using var store = new InstalledRuntimeStore(cache.Path);

        var read = await store.ReadAsync(CancellationToken.None);

        AssertEx.Null(read);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-runtime-state-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
