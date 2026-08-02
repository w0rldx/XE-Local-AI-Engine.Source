namespace XE_Local_AI_Engine.Tests.Images;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Images.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Behavioural tests for the image-job coordinator: queued persistence, serialize-to-one-running, both cancel modes,
///     the persist-before-succeed ordering, and the hub's late-subscriber replay.
/// </summary>
public sealed class ImageJobCoordinatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task EnqueueAsync_WhenValid_PersistsQueuedAndBuffersInitialEvent()
    {
        using var harness = Harness.Create(blockRuntime: true);

        var jobId = await harness.Coordinator.EnqueueAsync(NewInput("a queued job prompt"), CancellationToken.None).ConfigureAwait(false);

        // The queued row is persisted before EnqueueAsync returns, and the FIRST buffered replay event is Queued (seq 0)
        // regardless of any later transition the detached worker makes.
        var buffered = harness.Coordinator.SnapshotBufferedEvents(jobId);
        AssertEx.NotEmpty(buffered);
        var first = (ImageJobStatusHubEvent)buffered[0].Payload;
        AssertEx.Equal(ImageJobStatus.Queued.ToString(), first.Phase);
        AssertEx.Equal(expected: 0L, first.Seq);
        AssertEx.Equal(jobId, first.JobId);

        harness.Runtime.Release();
    }

    [Test]
    public async Task EnqueueAsync_WhenAJobIsRunning_HoldsTheSecondQueued()
    {
        using var harness = Harness.Create(blockRuntime: true);

        var first = await harness.Coordinator.EnqueueAsync(NewInput("first"), CancellationToken.None).ConfigureAwait(false);
        await harness.Runtime.Started.WaitAsync(Timeout).ConfigureAwait(false);

        var second = await harness.Coordinator.EnqueueAsync(NewInput("second"), CancellationToken.None).ConfigureAwait(false);

        // The single generation slot is held by the first (blocked) job, so the second job's detached run task is parked
        // on _generationSlot.WaitAsync and cannot reach the runtime or flip to Generating until the first releases. That
        // is a structural guarantee, not a timing one, so the second is observably still Queued with no wall-clock wait.
        var secondView = await harness.Coordinator.GetAsync(second, CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(ImageJobStatus.Queued, AssertEx.NotNull(secondView).Status);
        AssertEx.Equal(expected: 1, harness.Runtime.CallCount);

        var firstView = await harness.Coordinator.GetAsync(first, CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(ImageJobStatus.Generating, AssertEx.NotNull(firstView).Status);

        harness.Runtime.Release();
    }

    [Test]
    public async Task EnqueueAsync_WhenRuntimeMutationReserved_RejectsBeforePersistence()
    {
        using var harness = Harness.Create(blockRuntime: false, admitJobs: false);

        _ = await AssertEx.ThrowsAsync<ImageRuntimeBusyException>(() => harness.Coordinator.EnqueueAsync(NewInput("blocked-by-runtime-mutation"), CancellationToken.None)).ConfigureAwait(false);

        AssertEx.Empty(await harness.Coordinator.ListAsync(CancellationToken.None).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, harness.ActivityGate.ActiveLeaseCount);
    }

    [Test]
    public async Task CancelAsync_WhenQueued_NeverStartsTheRuntime()
    {
        using var harness = Harness.Create(blockRuntime: true);

        _ = await harness.Coordinator.EnqueueAsync(NewInput("running"), CancellationToken.None).ConfigureAwait(false);
        await harness.Runtime.Started.WaitAsync(Timeout).ConfigureAwait(false);

        var queued = await harness.Coordinator.EnqueueAsync(NewInput("queued-then-cancelled"), CancellationToken.None).ConfigureAwait(false);
        var cancelled = await harness.Coordinator.CancelAsync(queued, CancellationToken.None).ConfigureAwait(false);
        AssertEx.True(cancelled, "Cancelling a tracked queued job returns true.");

        await WaitForStatusAsync(harness, queued, ImageJobStatus.Cancelled).ConfigureAwait(false);

        // The runtime was only ever entered by the running job — the cancelled-while-queued job never called it.
        AssertEx.Equal(expected: 1, harness.Runtime.CallCount);
        AssertEx.False(harness.Runtime.Prompts.Contains("queued-then-cancelled"), "A cancelled queued job must never reach the runtime.");

        harness.Runtime.Release();
    }

    [Test]
    public async Task CancelAsync_WhenGenerating_CancelsTheRuntimeToken()
    {
        using var harness = Harness.Create(blockRuntime: true);

        var jobId = await harness.Coordinator.EnqueueAsync(NewInput("running-then-cancelled"), CancellationToken.None).ConfigureAwait(false);
        await harness.Runtime.Started.WaitAsync(Timeout).ConfigureAwait(false);

        var cancelled = await harness.Coordinator.CancelAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        AssertEx.True(cancelled, "Cancelling a tracked generating job returns true.");

        // The runtime was blocked awaiting the ct; cancellation unblocks it and the job lands Cancelled.
        await WaitForStatusAsync(harness, jobId, ImageJobStatus.Cancelled).ConfigureAwait(false);
        AssertEx.True(harness.Runtime.ObservedCancellation, "The runtime's cancellation token must have been signalled.");
    }

    [Test]
    public async Task GenerateAsync_WhenCompleted_PersistsImageThenMarksSucceeded()
    {
        using var harness = Harness.Create(blockRuntime: false);

        var jobId = await harness.Coordinator.EnqueueAsync(NewInput("completes"), CancellationToken.None).ConfigureAwait(false);

        await WaitForStatusAsync(harness, jobId, ImageJobStatus.Succeeded).ConfigureAwait(false);

        var view = AssertEx.NotNull(await harness.Coordinator.GetAsync(jobId, CancellationToken.None).ConfigureAwait(false));
        var imageId = AssertEx.NotNull((object?)view.ImageId, "A succeeded job records the produced image id.");
        // The image was persisted through the blob store, and its id matches the one stamped on the job.
        AssertEx.True(harness.Images.Added.TryGetValue((Guid)imageId, out var storedJobId), "The image must be persisted via the store.");
        AssertEx.Equal(jobId, storedJobId);
        AssertEx.NotNull((object?)view.DurationMs, "A succeeded job records its duration.");
        AssertEx.Equal(expected: 0, harness.ActivityGate.ActiveLeaseCount);
    }

    /// <summary>
    ///     F-030: the runtime rounds the requested size up (a requested 100x512 is produced as 128x512). The succeeded
    ///     job must record the dimensions the runtime reported producing, not the ones that were requested — otherwise
    ///     the job card describes an image that does not exist.
    /// </summary>
    [Test]
    public async Task GenerateAsync_WhenTheRuntimeRoundedTheSize_RecordsTheProducedDimensionsOnTheJob()
    {
        using var harness = Harness.Create(blockRuntime: false, producedSize: (128, 512));

        var input = NewInput("rounded-up") with
        {
            Width = 100,
            Height = 512
        };
        var jobId = await harness.Coordinator.EnqueueAsync(input, CancellationToken.None).ConfigureAwait(false);

        await WaitForStatusAsync(harness, jobId, ImageJobStatus.Succeeded).ConfigureAwait(false);

        var view = AssertEx.NotNull(await harness.Coordinator.GetAsync(jobId, CancellationToken.None).ConfigureAwait(false));
        AssertEx.Equal(expected: 128, view.Width, "A succeeded job must report the produced width (128), not the requested one (100).");
        AssertEx.Equal(expected: 512, view.Height);
    }

    [Test]
    public async Task DisposeAsync_WhenAJobIsGenerating_PersistsCancelledBeforeReturning()
    {
        using var harness = Harness.Create(blockRuntime: true);

        var jobId = await harness.Coordinator.EnqueueAsync(NewInput("interrupted-by-shutdown"), CancellationToken.None).ConfigureAwait(false);
        await harness.Runtime.Started.WaitAsync(Timeout).ConfigureAwait(false);

        // Graceful shutdown: DisposeAsync cancels the in-flight job and drains its run task, so the terminal Cancelled
        // state is persisted BEFORE DisposeAsync returns (no reliance on the startup reconciler for a clean shutdown).
        await harness.Coordinator.DisposeAsync().ConfigureAwait(false);

        AssertEx.Equal(ImageJobStatus.Cancelled, harness.Store.StatusOf(jobId));
        AssertEx.True(harness.Runtime.ObservedCancellation, "The runtime's cancellation token must have been signalled.");
    }

    [Test]
    public async Task EvictionTimer_WhenTerminalLogPassesRetentionOnAnIdleNode_EvictsWithoutANewEnqueue()
    {
        // The periodic eviction timer must release a terminal job's replay log after the retention window even when no
        // further job ever starts (before the timer existed, EnqueueAsync was the only eviction trigger, so the last
        // jobs' logs lingered on an idle node indefinitely).
        var timeProvider = new TimerCapturingTimeProvider();
        var runtime = new FakeImageRuntime(blockUntilReleased: false);
        var store = new FakeImageJobStore();

        var services = new ServiceCollection();
        services.AddScoped<IImageJobStore>(_ => store);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        using var coordinator = new ImageJobCoordinator(runtime,
            new FakeGeneratedImageStore(),
            scopeFactory,
            new NullImageJobEventPublisher(),
            timeProvider,
            NullLogger<ImageJobCoordinator>.Instance,
            new FakeImageRuntimeActivityGate());
        AssertEx.NotNull(timeProvider.EvictionCallback, "The coordinator must arm a periodic eviction timer at construction.");

        var jobId = await coordinator.EnqueueAsync(NewInput("idle-eviction"), CancellationToken.None).ConfigureAwait(false);
        await AssertEx.EventuallyAsync(() => store.StatusOf(jobId) == ImageJobStatus.Succeeded, Timeout,
            $"Job {jobId} did not reach status {ImageJobStatus.Succeeded}.").ConfigureAwait(false);
        AssertEx.NotEmpty(coordinator.SnapshotBufferedEvents(jobId));

        // Before the retention window elapses a timer tick must keep the log (late subscribers can still replay it).
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        timeProvider.FireEvictionTick();
        AssertEx.NotEmpty(coordinator.SnapshotBufferedEvents(jobId));

        // Past retention, the SAME idle-node tick path evicts it — no Enqueue involved. The terminal mark is stamped by
        // the run task, so tolerate the tiny window between the persisted status and the buffered terminal event.
        timeProvider.Advance(TimeSpan.FromMinutes(10));
        await AssertEx.EventuallyAsync(() =>
        {
            timeProvider.FireEvictionTick();
            return coordinator.SnapshotBufferedEvents(jobId).Count == 0;
        }, Timeout, "The idle eviction tick must release the terminal job's replay log after retention.").ConfigureAwait(false);
    }

    // Minimal TimeProvider for the eviction test: settable clock + captures the coordinator's periodic timer callback
    // so a test can fire ticks deterministically (the returned timer itself never fires on its own).
    private sealed class TimerCapturingTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;
        private object? _evictionState;

        public TimerCallback? EvictionCallback { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan by)
        {
            _utcNow += by;
        }

        public void FireEvictionTick()
        {
            EvictionCallback?.Invoke(_evictionState);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            EvictionCallback = callback;
            _evictionState = state;
            return new InertTimer();
        }

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                return true;
            }

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }

    [Test]
    public async Task ImageJobHub_WhenLateSubscriber_ReplaysBufferedEventsInSeqOrder()
    {
        var jobId = Guid.NewGuid();
        var event0 = new ImageJobStatusHubEvent(jobId, ImageJobStatus.Queued.ToString(), QueuePosition: null, ElapsedMs: null, ImageId: null, SanitizedError: null, OccurredAtUtc: 1, Seq: 0);
        var event1 = new ImageJobStatusHubEvent(jobId, ImageJobStatus.Generating.ToString(), QueuePosition: null, ElapsedMs: 0, ImageId: null, SanitizedError: null, OccurredAtUtc: 2, Seq: 1);

        var coordinator = Substitute.For<IImageJobCoordinator>();
        coordinator.SnapshotBufferedEvents(jobId).Returns(new[]
        {
            new ImageJobBufferedEvent(ImageJobHubEvents.StatusChanged, event0),
            new ImageJobBufferedEvent(ImageJobHubEvents.StatusChanged, event1)
        });

        var groups = Substitute.For<IGroupManager>();
        var clients = Substitute.For<IHubCallerClients>();
        var caller = Substitute.For<ISingleClientProxy>();
        clients.Caller.Returns(caller);
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns("conn-1");

        using var hub = new ImageJobHub(coordinator)
        {
            Groups = groups,
            Clients = clients,
            Context = context
        };

        await hub.Subscribe(jobId).ConfigureAwait(false);

        // Join-then-replay: the connection is added to the per-job group first, then every buffered event is replayed to
        // the caller in seq order.
        await groups.Received(1).AddToGroupAsync("conn-1", ImageJobHub.JobGroup(jobId), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        Received.InOrder(() =>
        {
            caller.SendCoreAsync(ImageJobHubEvents.StatusChanged, Arg.Is<object?[]>(args => args.Length == 1 && ReferenceEquals(args[0], event0)), Arg.Any<CancellationToken>());
            caller.SendCoreAsync(ImageJobHubEvents.StatusChanged, Arg.Is<object?[]>(args => args.Length == 1 && ReferenceEquals(args[0], event1)), Arg.Any<CancellationToken>());
        });
    }

    private static CreateImageJobInput NewInput(string prompt)
    {
        return new CreateImageJobInput
        {
            ModelName = "leejet/stable-diffusion-1.5-gguf",
            Prompt = prompt,
            Seed = -1,
            Width = 512,
            Height = 512,
            Steps = 20,
            Sampler = "euler_a",
            CfgScale = 7.0
        };
    }

    private static async Task WaitForStatusAsync(Harness harness, Guid jobId, ImageJobStatus status)
    {
        await AssertEx.EventuallyAsync(() => harness.Store.StatusOf(jobId) == status, Timeout,
            $"Job {jobId} did not reach status {status}.").ConfigureAwait(false);
    }

    private sealed record Harness(
        ImageJobCoordinator Coordinator,
        FakeImageRuntime Runtime,
        FakeImageJobStore Store,
        FakeGeneratedImageStore Images,
        FakeImageRuntimeActivityGate ActivityGate) : IDisposable
    {
        public void Dispose()
        {
            Coordinator.Dispose();
        }

        public static Harness Create(bool blockRuntime, bool admitJobs = true, (int Width, int Height)? producedSize = null)
        {
            var runtime = new FakeImageRuntime(blockRuntime, producedSize);
            var store = new FakeImageJobStore();
            var images = new FakeGeneratedImageStore();
            var activityGate = new FakeImageRuntimeActivityGate(admitJobs);

            var services = new ServiceCollection();
            services.AddScoped<IImageJobStore>(_ => store);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            // Ownership transfers to the returned Harness, whose Dispose disposes the coordinator (the using in each test).
#pragma warning disable CA2000
            var coordinator = new ImageJobCoordinator(runtime,
                images,
                scopeFactory,
                new NullImageJobEventPublisher(),
                TimeProvider.System,
                NullLogger<ImageJobCoordinator>.Instance,
                activityGate);
#pragma warning restore CA2000

            return new Harness(coordinator, runtime, store, images, activityGate);
        }
    }

    private sealed class FakeImageRuntimeActivityGate(bool admitJobs = true) : IImageRuntimeActivityGate
    {
        private int _activeLeaseCount;

        public int ActiveLeaseCount => Volatile.Read(ref _activeLeaseCount);

        public ImageRuntimeActivitySnapshot GetSnapshot()
        {
            return new ImageRuntimeActivitySnapshot(ActiveLeaseCount,
                SpawnReadinessCount: 0,
                ResidentProcessCount: 0,
                MutationReserved: !admitJobs,
                EvictionReserved: false);
        }

        public IImageRuntimeActivityLease? TryAcquireJobLease()
        {
            if (!admitJobs)
            {
                return null;
            }

            _ = Interlocked.Increment(ref _activeLeaseCount);
            return new Lease(this);
        }

        public IImageRuntimeActivityLease? TryAcquireSpawnReadinessLease()
        {
            throw new NotSupportedException();
        }

        public IImageRuntimeActivityLease? TryAcquireResidentProcessLease()
        {
            throw new NotSupportedException();
        }

        public IImageRuntimeActivityLease? TryAcquireEvictionReservation()
        {
            throw new NotSupportedException();
        }

        public IImageRuntimeActivityLease? TryAcquireMutationReservation()
        {
            throw new NotSupportedException();
        }

        private sealed class Lease(FakeImageRuntimeActivityGate owner) : IImageRuntimeActivityLease
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, value: 1) == 0)
                {
                    _ = Interlocked.Decrement(ref owner._activeLeaseCount);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    // producedSize models a runtime that returns an image whose size differs from the request (stable-diffusion.cpp
    // rounds up to a multiple of 64); null echoes the request, the pre-existing behaviour.
    private sealed class FakeImageRuntime(bool blockUntilReleased, (int Width, int Height)? producedSize = null) : IImageRuntime
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public bool ObservedCancellation { get; private set; }
        public ConcurrentBag<string> Prompts { get; } = [];

        public Task Started => _started.Task;

        public void Release()
        {
            _ = _release.TrySetResult();
        }

        public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, IProgress<ImageGenProgress> progress, CancellationToken ct)
        {
            _ = Interlocked.Increment(ref _callCount);
            Prompts.Add(request.Prompt);
            _ = _started.TrySetResult();

            progress.Report(new ImageGenProgress
            {
                Phase = ImageGenPhase.Generating,
                Elapsed = TimeSpan.Zero
            });

            try
            {
                if (blockUntilReleased)
                {
                    await _release.Task.WaitAsync(ct).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }

            return new ImageGenerationResult
            {
                ImageBytes = new byte[]
                {
                    1,
                    2,
                    3,
                    4
                },
                Width = producedSize?.Width ?? request.Width,
                Height = producedSize?.Height ?? request.Height,
                Seed = 42,
                Duration = TimeSpan.FromMilliseconds(7)
            };
        }
    }

    private sealed class FakeImageJobStore : IImageJobStore
    {
        private readonly ConcurrentDictionary<Guid, ImageJobView> _jobs = new();

        public ImageJobStatus? StatusOf(Guid jobId)
        {
            return _jobs.TryGetValue(jobId, out var view) ? view.Status : null;
        }

        public Task CreateQueuedAsync(ImageJobCreate create, CancellationToken cancellationToken)
        {
            _jobs[create.Id] = new ImageJobView
            {
                Id = create.Id,
                ModelName = create.ModelName,
                Prompt = create.Prompt,
                NegativePrompt = create.NegativePrompt,
                Seed = create.Seed,
                Width = create.Width,
                Height = create.Height,
                Steps = create.Steps,
                Sampler = create.Sampler,
                CfgScale = create.CfgScale,
                Status = ImageJobStatus.Queued,
                CreatedAtUtc = create.CreatedAtUtc
            };
            return Task.CompletedTask;
        }

        public Task<ImageJobView?> GetAsync(Guid jobId, CancellationToken cancellationToken)
        {
            return Task.FromResult(_jobs.TryGetValue(jobId, out var view) ? view : null);
        }

        public Task<IReadOnlyList<ImageJobView>> ListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ImageJobView>>(_jobs.Values.ToArray());
        }

        public Task MarkGeneratingAsync(Guid jobId, long startedAtUtc, CancellationToken cancellationToken)
        {
            Update(jobId, view => view with
            {
                Status = ImageJobStatus.Generating,
                StartedAtUtc = startedAtUtc
            });
            return Task.CompletedTask;
        }

        public Task MarkSucceededAsync(Guid jobId,
            Guid imageId,
            long completedAtUtc,
            long durationMs,
            int outputWidth,
            int outputHeight,
            CancellationToken cancellationToken)
        {
            Update(jobId, view => view with
            {
                Status = ImageJobStatus.Succeeded,
                ImageId = imageId,
                CompletedAtUtc = completedAtUtc,
                DurationMs = durationMs,
                Width = outputWidth > 0 ? outputWidth : view.Width,
                Height = outputHeight > 0 ? outputHeight : view.Height
            });
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid jobId, string sanitizedError, long completedAtUtc, CancellationToken cancellationToken)
        {
            Update(jobId, view => view with
            {
                Status = ImageJobStatus.Failed,
                SanitizedError = sanitizedError,
                CompletedAtUtc = completedAtUtc
            });
            return Task.CompletedTask;
        }

        public Task MarkCancelledAsync(Guid jobId, long completedAtUtc, CancellationToken cancellationToken)
        {
            Update(jobId, view => view with
            {
                Status = ImageJobStatus.Cancelled,
                CompletedAtUtc = completedAtUtc
            });
            return Task.CompletedTask;
        }

        public Task MarkCancellationRequestedAsync(Guid jobId, long requestedAtUtc, CancellationToken cancellationToken)
        {
            Update(jobId, view => view with
            {
                CancellationRequestedAtUtc = requestedAtUtc
            });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Guid>> MarkInterruptedFailedAsync(string sanitizedError, long completedAtUtc, CancellationToken cancellationToken)
        {
            var interrupted = _jobs.Values
                                   .Where(view => view.Status is ImageJobStatus.Queued or ImageJobStatus.Generating)
                                   .Select(view => view.Id)
                                   .ToArray();
            foreach (var jobId in interrupted)
            {
                Update(jobId, view => view with
                {
                    Status = ImageJobStatus.Failed,
                    SanitizedError = sanitizedError,
                    CompletedAtUtc = completedAtUtc
                });
            }

            return Task.FromResult<IReadOnlyList<Guid>>(interrupted);
        }

        private void Update(Guid jobId, Func<ImageJobView, ImageJobView> mutate)
        {
            if (_jobs.TryGetValue(jobId, out var view))
            {
                _jobs[jobId] = mutate(view);
            }
        }
    }

    private sealed class FakeGeneratedImageStore : IGeneratedImageStore
    {
        public ConcurrentDictionary<Guid, Guid> Added { get; } = new();

        public Task<GeneratedImageInfo> AddAsync(Guid jobId, Guid imageId, ReadOnlyMemory<byte> pngBytes, GeneratedImageMetadata metadata, CancellationToken cancellationToken)
        {
            Added[imageId] = jobId;
            return Task.FromResult(new GeneratedImageInfo(imageId, jobId, metadata.MimeType, metadata.Width, metadata.Height, pngBytes.Length, CreatedAtUtc: 0));
        }

        public Task<GeneratedImageContent?> OpenReadAsync(Guid imageId, CancellationToken cancellationToken)
        {
            return Task.FromResult<GeneratedImageContent?>(null);
        }
    }
}
