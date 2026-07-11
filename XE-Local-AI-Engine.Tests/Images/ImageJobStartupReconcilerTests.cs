namespace XE_Local_AI_Engine.Tests.Images;

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Images.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Behavioural tests for the image-job startup reconciler: jobs a previous process left Queued/Generating are
///     terminalized to Failed with the content-free interrupted reason (no auto-retry) and a status event is pushed;
///     terminal jobs are untouched; an empty store no-ops; a store failure never blocks startup. Runs against the real
///     EF/sqlite <see cref="ImageJobStore" /> so the non-terminal query and the prompt-ciphertext-preserving update are
///     covered end-to-end.
/// </summary>
public sealed class ImageJobStartupReconcilerTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task StartAsync_WithInterruptedAndTerminalJobs_FailsOnlyInterruptedAndPublishes()
    {
        await using var provider = await BuildProviderAsync().ConfigureAwait(false);
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var queuedId = await SeedJobAsync(scopeFactory, ImageJobStatus.Queued).ConfigureAwait(false);
        var generatingId = await SeedJobAsync(scopeFactory, ImageJobStatus.Generating).ConfigureAwait(false);
        var succeededId = await SeedJobAsync(scopeFactory, ImageJobStatus.Succeeded).ConfigureAwait(false);
        var cancelledId = await SeedJobAsync(scopeFactory, ImageJobStatus.Cancelled).ConfigureAwait(false);
        var failedId = await SeedJobAsync(scopeFactory, ImageJobStatus.Failed).ConfigureAwait(false);

        var publisher = new RecordingImageJobEventPublisher();
        var reconciler = NewReconciler(scopeFactory, publisher);

        await reconciler.StartAsync(CancellationToken.None).ConfigureAwait(false);

        // Queued + Generating are terminalized with the content-free interrupted reason and a completion timestamp.
        foreach (var interruptedId in new[]
                 {
                     queuedId,
                     generatingId
                 })
        {
            var view = AssertEx.NotNull(await GetAsync(scopeFactory, interruptedId).ConfigureAwait(false));
            AssertEx.Equal(ImageJobStatus.Failed, view.Status);
            AssertEx.Equal(ImageJobStartupReconciler.InterruptedReason, view.SanitizedError);
            AssertEx.NotNull((object?)view.CompletedAtUtc, "A reconciled job records its completion time.");
            AssertEx.Equal("a reconciler prompt", view.Prompt, "The prompt ciphertext must survive the status-only update.");
        }

        // Terminal jobs are untouched (status preserved, pre-existing error preserved).
        AssertEx.Equal(ImageJobStatus.Succeeded, AssertEx.NotNull(await GetAsync(scopeFactory, succeededId).ConfigureAwait(false)).Status);
        AssertEx.Equal(ImageJobStatus.Cancelled, AssertEx.NotNull(await GetAsync(scopeFactory, cancelledId).ConfigureAwait(false)).Status);
        var preFailed = AssertEx.NotNull(await GetAsync(scopeFactory, failedId).ConfigureAwait(false));
        AssertEx.Equal(ImageJobStatus.Failed, preFailed.Status);
        AssertEx.Equal("pre-existing failure", preFailed.SanitizedError);

        // Exactly one Failed event per interrupted job was pushed, carrying the interrupted reason (and never a prompt).
        AssertEx.Equal(expected: 2, publisher.Published.Count);
        foreach (var interruptedId in new[]
                 {
                     queuedId,
                     generatingId
                 })
        {
            AssertEx.ContainsSingle(publisher.Published, statusEvent => statusEvent.JobId == interruptedId
                                                                        && statusEvent.Phase == ImageJobStatus.Failed.ToString()
                                                                        && statusEvent.SanitizedError == ImageJobStartupReconciler.InterruptedReason);
        }
    }

    [Test]
    public async Task StartAsync_WhenOnlyTerminalJobsExist_TouchesNothingAndPublishesNothing()
    {
        await using var provider = await BuildProviderAsync().ConfigureAwait(false);
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var succeededId = await SeedJobAsync(scopeFactory, ImageJobStatus.Succeeded).ConfigureAwait(false);

        var publisher = new RecordingImageJobEventPublisher();
        var reconciler = NewReconciler(scopeFactory, publisher);

        await reconciler.StartAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(ImageJobStatus.Succeeded, AssertEx.NotNull(await GetAsync(scopeFactory, succeededId).ConfigureAwait(false)).Status);
        AssertEx.Empty(publisher.Published);
    }

    [Test]
    public async Task StartAsync_WhenTheStoreIsEmpty_NoOps()
    {
        await using var provider = await BuildProviderAsync().ConfigureAwait(false);
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var publisher = new RecordingImageJobEventPublisher();
        var reconciler = NewReconciler(scopeFactory, publisher);

        await reconciler.StartAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Empty(publisher.Published);
    }

    [Test]
    public async Task StartAsync_WhenTheStoreThrows_SwallowsAndPublishesNothing()
    {
        // Reconciliation is best-effort: a store failure must never block node startup.
        var store = Substitute.For<IImageJobStore>();
        _ = store.MarkInterruptedFailedAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                 .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var services = new ServiceCollection();
        services.AddScoped<IImageJobStore>(_ => store);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var publisher = new RecordingImageJobEventPublisher();
        var reconciler = NewReconciler(scopeFactory, publisher);

        await reconciler.StartAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Empty(publisher.Published);
    }

    private static ImageJobStartupReconciler NewReconciler(IServiceScopeFactory scopeFactory, IImageJobEventPublisher publisher)
    {
        return new ImageJobStartupReconciler(scopeFactory,
            publisher,
            TimeProvider.System,
            NullLogger<ImageJobStartupReconciler>.Instance);
    }

    private static async Task<Guid> SeedJobAsync(IServiceScopeFactory scopeFactory, ImageJobStatus status)
    {
        var jobId = Guid.NewGuid();

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IImageJobStore>();
        await store.CreateQueuedAsync(new ImageJobCreate
        {
            Id = jobId,
            ModelName = "leejet/stable-diffusion-1.5-gguf",
            Prompt = "a reconciler prompt",
            Seed = -1,
            Width = 512,
            Height = 512,
            Steps = 20,
            Sampler = "euler_a",
            CfgScale = 7.0,
            CreatedAtUtc = 1
        }, CancellationToken.None).ConfigureAwait(false);

        switch (status)
        {
            case ImageJobStatus.Queued:
                break;
            case ImageJobStatus.Generating:
                await store.MarkGeneratingAsync(jobId, startedAtUtc: 2, CancellationToken.None).ConfigureAwait(false);
                break;
            case ImageJobStatus.Succeeded:
                await store.MarkGeneratingAsync(jobId, startedAtUtc: 2, CancellationToken.None).ConfigureAwait(false);
                await store.MarkSucceededAsync(jobId, Guid.NewGuid(), completedAtUtc: 3, durationMs: 1, CancellationToken.None).ConfigureAwait(false);
                break;
            case ImageJobStatus.Failed:
                await store.MarkFailedAsync(jobId, "pre-existing failure", completedAtUtc: 3, CancellationToken.None).ConfigureAwait(false);
                break;
            case ImageJobStatus.Cancelled:
                await store.MarkCancelledAsync(jobId, completedAtUtc: 3, CancellationToken.None).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, message: null);
        }

        return jobId;
    }

    private static async Task<ImageJobView?> GetAsync(IServiceScopeFactory scopeFactory, Guid jobId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IImageJobStore>();
        return await store.GetAsync(jobId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<ServiceProvider> BuildProviderAsync()
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "image-jobs.sqlite");

        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddScoped<IImageJobStore, ImageJobStore>();

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        return provider;
    }

    private sealed class RecordingImageJobEventPublisher : IImageJobEventPublisher
    {
        public ConcurrentQueue<ImageJobStatusHubEvent> Published { get; } = new();

        public Task PublishStatusAsync(ImageJobStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
        {
            Published.Enqueue(statusEvent);
            return Task.CompletedTask;
        }
    }
}
