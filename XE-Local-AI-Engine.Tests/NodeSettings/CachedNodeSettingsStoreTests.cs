namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The cache decorator must turn repeated loads into a single inner read and drop the entry on every write, so a
///     read after a save reflects what the canonical inner store persisted. The entry has no TTL, so the ordering
///     tests below are not about a transient blip: a publication that lands out of order is permanent.
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
    public async Task InterleavedWrites_CannotLeaveTheCacheBehindTheStore()
    {
        // The cache has no TTL, so a publication that lands out of order is not a brief blip — it is permanent. Here
        // the FIRST write's continuation runs LAST: when a write published its own value, it overwrote the newer one
        // and every subsequent read (the reconciliation pass's cheap pre-check included) saw settings that were no
        // longer on disk.
        var inner = new OrderedNodeSettingsStore();
        inner.HoldFirstWrite();
        using var cache = NewCache();
        var sut = new CachedNodeSettingsStore(inner, cache);

        var slow = sut.SaveAsync(new StoredNodeSettings { DefaultModelName = "first" });
        await inner.WaitUntilWritingAsync();
        await sut.SaveAsync(new StoredNodeSettings { DefaultModelName = "second" });
        inner.ReleaseFirstWrite();
        await slow;

        AssertEx.Equal("second", (await sut.LoadAsync()).DefaultModelName);
    }

    [Test]
    public async Task Load_ThatOverlapsAWrite_DoesNotPublishItsStaleRead()
    {
        var inner = new OrderedNodeSettingsStore();
        inner.HoldFirstRead();
        using var cache = NewCache();
        var sut = new CachedNodeSettingsStore(inner, cache);

        // The load reads the pre-write value, a write lands while it is in flight, and the load then tries to publish.
        var load = sut.LoadAsync();
        await inner.WaitUntilReadingAsync();
        await sut.SaveAsync(new StoredNodeSettings { DefaultModelName = "written" });
        inner.ReleaseRead();
        AssertEx.Null((await load).DefaultModelName);

        AssertEx.Equal("written", (await sut.LoadAsync()).DefaultModelName);
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

    /// <summary>
    ///     An in-memory store whose first write and first read can be held open, so a test can force the interleavings
    ///     the decorator has to survive. The stored value is what a subsequent read returns, exactly as disk would.
    /// </summary>
    private sealed class OrderedNodeSettingsStore : INodeSettingsStore
    {
        private readonly TaskCompletionSource _firstWriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();

        private StoredNodeSettings _current = new();
        private bool _holdFirstRead;
        private bool _holdFirstWrite;
        private int _writes;

        /// <summary>Holds the first read open until <see cref="ReleaseRead" />.</summary>
        public void HoldFirstRead()
        {
            _holdFirstRead = true;
        }

        /// <summary>Holds the first write's CALLER open — after it has committed — until <see cref="ReleaseFirstWrite" />.</summary>
        public void HoldFirstWrite()
        {
            _holdFirstWrite = true;
        }

        public Task WaitUntilWritingAsync()
        {
            return _firstWriteStarted.Task;
        }

        public void ReleaseFirstWrite()
        {
            _ = _firstWriteGate.TrySetResult();
        }

        public Task WaitUntilReadingAsync()
        {
            return _readStarted.Task;
        }

        public void ReleaseRead()
        {
            _ = _readGate.TrySetResult();
        }

        public async Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            // Snapshot FIRST, then hold: the read this models already happened, and what the test controls is when its
            // caller gets to act on it.
            var snapshot = Load(cancellationToken);
            if (_holdFirstRead && _readStarted.TrySetResult())
            {
                await _readGate.Task.ConfigureAwait(false);
            }

            return snapshot;
        }

        public StoredNodeSettings Load(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return _current;
            }
        }

        public async Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
        {
            // Commit FIRST, then hold: the disk write this models has already landed, and what the gate controls is
            // when its caller's continuation — the cache publication — gets to run.
            lock (_gate)
            {
                _current = settings;
            }

            if (_holdFirstWrite && Interlocked.Increment(ref _writes) == 1)
            {
                _ = _firstWriteStarted.TrySetResult();
                await _firstWriteGate.Task.ConfigureAwait(false);
            }
        }

        public async Task<StoredNodeSettings> UpdateAsync(Func<StoredNodeSettings, StoredNodeSettings> mutate, CancellationToken cancellationToken = default)
        {
            StoredNodeSettings mutated;
            lock (_gate)
            {
                mutated = mutate(_current);
            }

            await SaveAsync(mutated, cancellationToken).ConfigureAwait(false);
            return mutated;
        }
    }
}
