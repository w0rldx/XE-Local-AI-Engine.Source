namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class ImageJobStoreTests : IDisposable
{
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task List_ActiveOnly_ReturnsTheQueuedAndGeneratingJobsBeyondTheFirstPage()
    {
        await using var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, "image-jobs.sqlite"), _keyHolder);
        _ = await context.Database.EnsureCreatedAsync();
        var store = new ImageJobStore(context);

        // The two oldest stay active, behind 28 newer finished jobs: more than the 20 the job feed's page held.
        var ids = new List<Guid>();
        for (var index = 0; index < 30; index++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await store.CreateQueuedAsync(new ImageJobCreate
            {
                Id = id,
                ModelName = "leejet/stable-diffusion-1.5-gguf",
                Prompt = $"prompt {index}",
                Seed = -1,
                Width = 512,
                Height = 512,
                Steps = 20,
                Sampler = "euler_a",
                CfgScale = 7.0,
                CreatedAtUtc = index
            }, CancellationToken.None);
            if (index >= 2)
            {
                await (index % 2 == 0
                    ? store.MarkFailedAsync(id, "failed", completedAtUtc: 100, CancellationToken.None)
                    : store.MarkCancelledAsync(id, completedAtUtc: 100, CancellationToken.None));
            }
        }

        await store.MarkGeneratingAsync(ids[0], startedAtUtc: 50, CancellationToken.None);

        var active = await store.ListAsync(limit: 20, offset: 0, activeOnly: true, CancellationToken.None);

        AssertEx.True(active.Select(view => view.Id).SequenceEqual([ids[1], ids[0]]), "Only the two active jobs, newest first.");
        AssertEx.True(active.Select(view => view.Status).SequenceEqual([ImageJobStatus.Queued, ImageJobStatus.Generating]));
        AssertEx.Equal(expected: 2, await store.CountAsync(activeOnly: true, CancellationToken.None));
        AssertEx.Equal(expected: 30, await store.CountAsync(activeOnly: false, CancellationToken.None));
    }
}
