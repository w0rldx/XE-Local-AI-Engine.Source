namespace XE_Local_AI_Engine.Tests.Images;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Proves a succeeded job records the seed the runtime actually drew, not the request's random sentinel.
///     <para>
///         A job submitted with <c>seed = -1</c> asks the runtime to pick one. Before this write-back the row kept
///         <c>-1</c> forever, so the seed that produced the image was lost and the result could never be reproduced —
///         live-observed in the viewer, which showed "Seed -1" for every randomly-seeded image.
///     </para>
/// </summary>
public sealed class ImageJobSeedWriteBackTests : IDisposable
{
    private const long RandomSeedSentinel = -1;

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task MarkSucceededAsync_WhenSeedWasRandom_RecordsTheResolvedSeed()
    {
        await using var provider = await BuildProviderAsync().ConfigureAwait(false);
        var jobId = Guid.NewGuid();
        await CreateQueuedJobAsync(provider, jobId, RandomSeedSentinel).ConfigureAwait(false);

        await MarkSucceededAsync(provider, jobId, resolvedSeed: 182_736).ConfigureAwait(false);

        var view = await GetAsync(provider, jobId).ConfigureAwait(false);
        AssertEx.Equal(182_736, view.Seed);
    }

    [Test]
    public async Task MarkSucceededAsync_WhenRuntimeReportedNoSeed_LeavesTheRequestedSeedAlone()
    {
        await using var provider = await BuildProviderAsync().ConfigureAwait(false);
        var jobId = Guid.NewGuid();
        await CreateQueuedJobAsync(provider, jobId, seed: 4242).ConfigureAwait(false);

        // A negative resolved seed means the runtime could not report one; overwriting would replace a usable seed
        // with a second sentinel.
        await MarkSucceededAsync(provider, jobId, resolvedSeed: RandomSeedSentinel).ConfigureAwait(false);

        var view = await GetAsync(provider, jobId).ConfigureAwait(false);
        AssertEx.Equal(4242, view.Seed);
    }

    [Test]
    public async Task MarkSucceededAsync_PreservesAnExplicitSeedTheRuntimeEchoedBack()
    {
        await using var provider = await BuildProviderAsync().ConfigureAwait(false);
        var jobId = Guid.NewGuid();
        await CreateQueuedJobAsync(provider, jobId, seed: 99).ConfigureAwait(false);

        await MarkSucceededAsync(provider, jobId, resolvedSeed: 99).ConfigureAwait(false);

        var view = await GetAsync(provider, jobId).ConfigureAwait(false);
        AssertEx.Equal(99, view.Seed);
    }

    // Seed 0 is a legitimate seed and must survive: a `> 0` guard instead of `>= 0` would silently drop it.
    [Test]
    public async Task MarkSucceededAsync_RecordsZeroAsARealSeed()
    {
        await using var provider = await BuildProviderAsync().ConfigureAwait(false);
        var jobId = Guid.NewGuid();
        await CreateQueuedJobAsync(provider, jobId, RandomSeedSentinel).ConfigureAwait(false);

        await MarkSucceededAsync(provider, jobId, resolvedSeed: 0).ConfigureAwait(false);

        var view = await GetAsync(provider, jobId).ConfigureAwait(false);
        AssertEx.Equal(0, view.Seed);
    }

    private static async Task CreateQueuedJobAsync(ServiceProvider provider, Guid jobId, long seed)
    {
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = new ImageJobStore(scope.ServiceProvider.GetRequiredService<NodeChatDbContext>());
        await store.CreateQueuedAsync(new ImageJobCreate
        {
            Id = jobId,
            ModelName = "sd-1.5",
            Prompt = "a red fox in snow",
            Seed = seed,
            Width = 512,
            Height = 512,
            Steps = 20,
            Sampler = "euler_a",
            CfgScale = 7.0,
            CreatedAtUtc = 100
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task MarkSucceededAsync(ServiceProvider provider, Guid jobId, long resolvedSeed)
    {
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = new ImageJobStore(scope.ServiceProvider.GetRequiredService<NodeChatDbContext>());
        await store.MarkSucceededAsync(jobId,
            Guid.NewGuid(),
            completedAtUtc: 200,
            durationMs: 12_000,
            outputWidth: 512,
            outputHeight: 512,
            resolvedSeed,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<ImageJobView> GetAsync(ServiceProvider provider, Guid jobId)
    {
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = new ImageJobStore(scope.ServiceProvider.GetRequiredService<NodeChatDbContext>());
        return AssertEx.NotNull(await store.GetAsync(jobId, CancellationToken.None).ConfigureAwait(false));
    }

    private async Task<ServiceProvider> BuildProviderAsync()
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "image-jobs.sqlite");

        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        return provider;
    }
}
