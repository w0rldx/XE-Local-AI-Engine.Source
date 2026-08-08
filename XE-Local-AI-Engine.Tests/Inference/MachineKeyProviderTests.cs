namespace XE_Local_AI_Engine.Tests.Inference;

using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="MachineKeyProvider" /> tests: the key is generated and persisted exactly once on first use, then the
///     cached value is returned byte-for-byte on every later read (no re-generation, no second write). Backed by a tiny
///     in-memory settings store that counts writes.
/// </summary>
public sealed class MachineKeyProviderTests
{
    [Test]
    public async Task MachineKey_IsStableAcrossReads_AndPersistsOnce()
    {
        var store = new FakeNodeSettingsStore();
        using var provider = new MachineKeyProvider(store);

        var first = await provider.GetMachineKeyAsync(CancellationToken.None);
        var second = await provider.GetMachineKeyAsync(CancellationToken.None);

        AssertEx.NotNullOrEmpty(first);
        AssertEx.Equal(first, second);
        // Generated and persisted exactly once; the second read is served from cache.
        AssertEx.Equal(1, store.SaveCount);
        AssertEx.Equal(first, store.Current.MachineKey);
    }

    // Minimal stateful INodeSettingsStore: holds the last-saved settings and counts writes. No file I/O.
    private sealed class FakeNodeSettingsStore : INodeSettingsStore
    {
        public StoredNodeSettings Current { get; private set; } = new();

        public int SaveCount { get; private set; }

        public Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Current);
        }

        public StoredNodeSettings Load(CancellationToken cancellationToken = default)
        {
            return Current;
        }

        public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            SaveCount++;
            return Task.CompletedTask;
        }
    }
}
