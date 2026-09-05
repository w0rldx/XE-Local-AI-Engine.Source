namespace XE_Local_AI_Engine.Tests.Inference;

using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="MachineKeyProvider" /> tests: the key is generated and persisted exactly once on first use, then the
///     cached value is returned byte-for-byte on every later read (no re-generation, no second write) — and a key a
///     racing writer minted first is ADOPTED rather than overwritten, because inference profiles are keyed by it.
///     Backed by a tiny in-memory settings store that counts writes and serializes its read-modify-write the way the
///     real store's lock does.
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

    [Test]
    public async Task MachineKey_WhenAKeyExists_IsNotRewritten()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            MachineKey = "already-minted"
        });
        using var provider = new MachineKeyProvider(store);

        var key = await provider.GetMachineKeyAsync(CancellationToken.None);

        AssertEx.Equal("already-minted", key);
        AssertEx.Equal(0, store.SaveCount, "reading an existing key must not write the settings file.");
    }

    [Test]
    public async Task MachineKey_WhenTwoProvidersMintConcurrently_AgreeOnTheOneStoredKey()
    {
        // Two providers on one node — a singleton and a startup-path instance, or simply two racing first callers —
        // both see no key and both go on to mint. Each provider's own gate serializes nothing across the pair, so
        // only the STORE's read-modify-write can decide: the first write mints, the second adopts. Two different keys
        // here would orphan every frozen inference profile, since profiles are keyed by machine key.
        var store = new FakeNodeSettingsStore();
        using var first = new MachineKeyProvider(store);
        using var second = new MachineKeyProvider(store);

        // Neither provider leaves its load until both have loaded, so both really did see an empty key.
        var bothLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        store.AfterLoad = () =>
        {
            if (Interlocked.Increment(ref loads) == 2)
            {
                bothLoaded.TrySetResult();
            }

            return bothLoaded.Task;
        };

        var keys = await Task.WhenAll(Task.Run(() => first.GetMachineKeyAsync(CancellationToken.None)),
            Task.Run(() => second.GetMachineKeyAsync(CancellationToken.None)));

        AssertEx.NotNullOrEmpty(keys[0]);
        AssertEx.Equal(keys[0], keys[1], "both providers must return the one key the store ended up holding.");
        AssertEx.Equal(keys[0], store.Current.MachineKey);
    }

    // Minimal stateful INodeSettingsStore: holds the last-saved settings, counts writes, and (like the real store)
    // applies a mutation under one lock so two concurrent read-modify-writes cannot interleave. No file I/O.
    private sealed class FakeNodeSettingsStore(StoredNodeSettings? initial = null) : INodeSettingsStore
    {
        private readonly Lock _gate = new();

        public StoredNodeSettings Current { get; private set; } = initial ?? new StoredNodeSettings();

        public int SaveCount { get; private set; }

        /// <summary>Runs after every load, so a test can hold both callers at that point and force the real overlap.</summary>
        public Func<Task>? AfterLoad { get; set; }

        public async Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            var current = Current;
            if (AfterLoad is { } afterLoad)
            {
                await afterLoad();
            }

            return current;
        }

        public StoredNodeSettings Load(CancellationToken cancellationToken = default)
        {
            return Current;
        }

        public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Current = settings;
                SaveCount++;
            }

            return Task.CompletedTask;
        }

        public Task<StoredNodeSettings> UpdateAsync(Func<StoredNodeSettings, StoredNodeSettings> mutate, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(mutate);

            // The real store holds ONE lock across load-mutate-save; a fake that only locks the save would let the
            // second minter overwrite the first and this test would pass on a broken provider.
            lock (_gate)
            {
                Current = mutate(Current);
                SaveCount++;
                return Task.FromResult(Current);
            }
        }
    }
}
