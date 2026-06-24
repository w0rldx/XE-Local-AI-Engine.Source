namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The cache decorator must turn repeated loads into a single inner read, invalidate on save, and re-prime the cache
///     from the canonical (normalized) inner store so a read after a save reflects what was persisted.
/// </summary>
public sealed class CachedNodeSettingsStoreTests
{
    private static MemoryCache NewCache()
    {
        return new MemoryCache(new MemoryCacheOptions());
    }

    [Test]
    public async Task LoadTwice_HitsInnerStoreOnce()
    {
        var inner = Substitute.For<INodeSettingsStore>();
        inner.LoadAsync(Arg.Any<CancellationToken>())
             .Returns(new StoredNodeSettings
             {
                 MaxMessageRequestTimeoutSeconds = 120
             });
        using var cache = NewCache();
        var sut = new CachedNodeSettingsStore(inner, cache);

        var first = await sut.LoadAsync();
        var second = await sut.LoadAsync();

        AssertEx.Equal(expected: 120, first.MaxMessageRequestTimeoutSeconds);
        AssertEx.Equal(expected: 120, second.MaxMessageRequestTimeoutSeconds);
        await inner.Received(1).LoadAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Save_InvalidatesCache_AndSubsequentLoadReturnsPersistedValue()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "xe-cached-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        try
        {
            using var inner = new NodeSettingsStore(new FakeNodeDataDirectory(dataDir), NullLogger<NodeSettingsStore>.Instance);
            using var cache = NewCache();
            var sut = new CachedNodeSettingsStore(inner, cache);

            // Prime the cache with the on-disk-absent default.
            var initial = await sut.LoadAsync();
            AssertEx.Null(initial.DefaultModelName);

            await sut.SaveAsync(new StoredNodeSettings
            {
                DefaultModelName = "my-model",
                MaxMessageRequestTimeoutSeconds = 120
            });

            var reloaded = await sut.LoadAsync();
            AssertEx.Equal("my-model", reloaded.DefaultModelName);
            AssertEx.Equal(expected: 120, reloaded.MaxMessageRequestTimeoutSeconds);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Test]
    public async Task Save_DelegatesToInnerStore()
    {
        var inner = Substitute.For<INodeSettingsStore>();
        inner.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        using var cache = NewCache();
        var sut = new CachedNodeSettingsStore(inner, cache);

        await sut.SaveAsync(new StoredNodeSettings
        {
            DefaultModelName = "x"
        });

        await inner.Received(1).SaveAsync(Arg.Is<StoredNodeSettings>(s => s.DefaultModelName == "x"), Arg.Any<CancellationToken>());
    }
}
