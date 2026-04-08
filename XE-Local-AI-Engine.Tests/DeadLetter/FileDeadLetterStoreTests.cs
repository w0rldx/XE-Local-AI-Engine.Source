namespace XE_Local_AI_Engine.Tests.DeadLetter;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Configuration;
using XE_Local_AI_Engine.Models;
using XE_Local_AI_Engine.Services.DeadLetter;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class FileDeadLetterStoreTests : IDisposable
{
    private readonly string _queuePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_queuePath))
        {
            Directory.Delete(_queuePath, recursive: true);
        }
    }

    [Test]
    public async Task EnqueueAsync_WritesJsonFileToDisk()
    {
        using var store = CreateStore();

        await store.EnqueueAsync(CreatePayload());

        AssertEx.Equal(1, Directory.GetFiles(_queuePath, "*.json").Length);
    }

    [Test]
    public async Task GetPendingAsync_ReturnsAllEnqueued()
    {
        using var store = CreateStore();
        var first = CreatePayload();
        var second = CreatePayload();

        await store.EnqueueAsync(first);
        await store.EnqueueAsync(second);

        var pending = await store.GetPendingAsync();

        AssertEx.Equal(2, pending.Count);
        AssertEx.Contains(pending, payload => payload.InvocationId == first.InvocationId);
        AssertEx.Contains(pending, payload => payload.InvocationId == second.InvocationId);
    }

    [Test]
    public async Task RemoveAsync_DeletesFile()
    {
        using var store = CreateStore();
        var payload = CreatePayload();
        await store.EnqueueAsync(payload);

        await store.RemoveAsync(payload.InvocationId);

        AssertEx.Equal(0, Directory.GetFiles(_queuePath, "*.json").Length);
    }

    [Test]
    public async Task GetCurrentSizeBytes_ReturnsPositiveAfterEnqueue()
    {
        using var store = CreateStore();
        await store.EnqueueAsync(CreatePayload());

        AssertEx.True(store.GetCurrentSizeBytes() > 0);
    }

    [Test]
    public async Task EnqueueAsync_WhenSizeLimitExceeded_EvictsOldestFile()
    {
        Directory.CreateDirectory(_queuePath);
        var oldestFileName = $"20000101000000000-{Guid.NewGuid():N}.json";
        await File.WriteAllTextAsync(Path.Combine(_queuePath, oldestFileName), "{}\n");
        using (var stream = File.OpenWrite(Path.Combine(_queuePath, $"20000101000000001-{Guid.NewGuid():N}.json")))
        {
            stream.SetLength((100L * 1024 * 1024) + 1024);
        }

        using var store = CreateStore();
        var payload = CreatePayload();
        await store.EnqueueAsync(payload);

        var files = Directory.GetFiles(_queuePath, "*.json").Select(Path.GetFileName).ToArray();
        AssertEx.False(files.Contains(oldestFileName));

        var pending = await store.GetPendingAsync();
        AssertEx.Contains(pending, entry => entry.InvocationId == payload.InvocationId);
        AssertEx.True(store.GetCurrentSizeBytes() <= (100L * 1024 * 1024));
    }

    [Test]
    public async Task EnqueueAsync_ConcurrentCalls_SemaphoreSerializesAccess()
    {
        using var store = CreateStore();

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.EnqueueAsync(CreatePayload())));

        var pending = await store.GetPendingAsync();
        AssertEx.Equal(10, pending.Count);
    }

    [Test]
    public async Task GetPendingAsync_CorruptJsonFile_SkipsAndReturnsRest()
    {
        using var store = CreateStore();
        var payload = CreatePayload();
        await store.EnqueueAsync(payload);
        await File.WriteAllTextAsync(Path.Combine(_queuePath, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json"), "not-json");

        var pending = await store.GetPendingAsync();

        AssertEx.Equal(1, pending.Count);
        AssertEx.Equal(payload.InvocationId, pending[0].InvocationId);
    }

    [Test]
    public async Task EnqueueAsync_CreatesDirectoryIfNotExists()
    {
        using var store = CreateStore();
        Directory.Delete(_queuePath, recursive: true);

        await store.EnqueueAsync(CreatePayload());

        AssertEx.True(Directory.Exists(_queuePath));
    }

    private FileDeadLetterStore CreateStore()
    {
        return new FileDeadLetterStore(
            Options.Create(new WorkerNodeOptions
            {
                NodeName = "worker",
                DeadLetterQueuePath = _queuePath,
            }),
            NullLogger<FileDeadLetterStore>.Instance);
    }

    private static InvocationFailedPayload CreatePayload()
    {
        return new InvocationFailedPayload
        {
            InvocationId = Guid.NewGuid(),
            Error = "boom",
        };
    }
}
