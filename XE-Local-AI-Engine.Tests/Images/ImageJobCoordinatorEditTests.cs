namespace XE_Local_AI_Engine.Tests.Images;

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Images.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Edit jobs in the coordinator: the enqueue validation matrix, source bytes read only once the job holds the slot,
///     a vanished source and an unsupported runtime build failing with fixed messages, and the persisted edit columns.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ImageJobCoordinatorEditTests
{
    private const string Sd15Model = "sd-1.5";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] SourceBytes = [9, 8, 7, 6, 5];

    [Test]
    public async Task EnqueueAsync_WithInconsistentOrUnsupportedEdit_IsRejectedBeforeAnythingIsPersisted()
    {
        var source = Guid.NewGuid();
        (CreateImageJobInput Input, string Message)[] cases =
        [
            (EditInput(ImageEditMode.Img2Img, sourceImageId: null), "An edit mode requires a source image."),
            (EditInput(mode: null, source), "A source image requires an edit mode."),
            (EditInput(mode: null, sourceImageId: null, strength: 0.5), "Strength applies only to img2img edits."),
            (EditInput(ImageEditMode.Reference, source, strength: 0.5), "Strength applies only to img2img edits."),
            (EditInput(ImageEditMode.Img2Img, source, strength: 1.5), "Strength must be between 0 and 1."),
            (EditInput(ImageEditMode.Img2Img, source, strength: -0.1), "Strength must be between 0 and 1."),
            // Sd15 offers img2img only.
            (EditInput(ImageEditMode.Reference, source), "The selected model does not support this edit mode.")
        ];

        foreach (var (input, message) in cases)
        {
            using var harness = Harness.Create(sourceRowExists: true);

            var rejected = await AssertEx.ThrowsAsync<ImageJobInputRejectedException>(() => harness.Coordinator.EnqueueAsync(input, CancellationToken.None));

            AssertEx.Equal(message, rejected.Message);
            AssertEx.Empty(harness.Store.Created, $"A refused job must not be persisted ({message}).");
            AssertEx.Equal(expected: 0, harness.Runtime.CallCount);
        }
    }

    [Test]
    public async Task EnqueueAsync_WhenTheSourceRowDoesNotExist_IsRejected()
    {
        using var harness = Harness.Create(sourceRowExists: false);

        var rejected = await AssertEx.ThrowsAsync<ImageJobInputRejectedException>(() =>
            harness.Coordinator.EnqueueAsync(EditInput(ImageEditMode.Img2Img, Guid.NewGuid()), CancellationToken.None));

        AssertEx.Equal("The source image does not exist.", rejected.Message);
        AssertEx.Empty(harness.Store.Created);
    }

    [Test]
    public async Task EnqueueAsync_ForAModelMissingFromTheRegistry_StillOffersImg2Img()
    {
        using var harness = Harness.Create(sourceRowExists: true, sourceBytes: SourceBytes);

        var input = EditInput(ImageEditMode.Img2Img, Guid.NewGuid()) with
        {
            ModelName = "not-installed"
        };
        var jobId = await harness.Coordinator.EnqueueAsync(input, CancellationToken.None);

        await WaitForStatusAsync(harness, jobId, ImageJobStatus.Succeeded);
    }

    [Test]
    public async Task Img2ImgJob_ReadsTheSourceOnlyOnceItHoldsTheSlot_AndHandsTheBytesToTheRuntime()
    {
        using var harness = Harness.Create(sourceRowExists: true, sourceBytes: SourceBytes, blockFirstCall: true);
        var sourceId = Guid.NewGuid();

        // A text-to-image job takes the slot and blocks inside the runtime, so the edit job queues behind it.
        var blocker = await harness.Coordinator.EnqueueAsync(TextInput(), CancellationToken.None);
        await harness.Runtime.FirstCallStarted.WaitAsync(Timeout);

        var editJob = await harness.Coordinator.EnqueueAsync(EditInput(ImageEditMode.Img2Img, sourceId, strength: 0.4), CancellationToken.None);
        await harness.Images.DidNotReceiveWithAnyArgs().OpenReadAsync(Guid.Empty, CancellationToken.None);

        harness.Runtime.ReleaseFirstCall();
        await WaitForStatusAsync(harness, blocker, ImageJobStatus.Succeeded);
        await WaitForStatusAsync(harness, editJob, ImageJobStatus.Succeeded);

        await harness.Images.Received(1).OpenReadAsync(sourceId, Arg.Any<CancellationToken>());
        var request = AssertEx.NotNull(harness.Runtime.Requests.LastOrDefault(static r => r.Mode is not null));
        AssertEx.Equal(ImageEditMode.Img2Img, request.Mode);
        AssertEx.Equal(0.4, request.Strength);
        AssertEx.True(request.InitImage is { } init && init.Span.SequenceEqual(SourceBytes), "The source bytes go to the runtime as the init image.");
        AssertEx.Null(request.ReferenceImage);
    }

    [Test]
    public async Task EditJob_WhenTheSourceVanishesBeforeItRuns_FailsWithTheFixedMessageAndNeverCallsTheRuntime()
    {
        using var harness = Harness.Create(sourceRowExists: true, sourceBytes: null);

        var jobId = await harness.Coordinator.EnqueueAsync(EditInput(ImageEditMode.Img2Img, Guid.NewGuid()), CancellationToken.None);

        await WaitForStatusAsync(harness, jobId, ImageJobStatus.Failed);
        AssertEx.Equal("The source image no longer exists.", harness.Store.FailureOf(jobId));
        AssertEx.Equal(expected: 0, harness.Runtime.CallCount);
    }

    [Test]
    public async Task EditJob_WhenTheRuntimeBuildLacksTheFeature_FailsWithTheFixedUnsupportedMessage()
    {
        using var harness = Harness.Create(sourceRowExists: true,
            sourceBytes: SourceBytes,
            runtimeFailure: new StableDiffusionRuntimeException("internal detail that must not reach the operator")
            {
                FeatureUnsupported = true
            });

        var jobId = await harness.Coordinator.EnqueueAsync(EditInput(ImageEditMode.Img2Img, Guid.NewGuid()), CancellationToken.None);

        await WaitForStatusAsync(harness, jobId, ImageJobStatus.Failed);
        AssertEx.Equal("The image runtime build does not support this edit mode.", harness.Store.FailureOf(jobId));
    }

    [Test]
    public async Task EnqueueAsync_PersistsTheEditColumns()
    {
        using var harness = Harness.Create(sourceRowExists: true, sourceBytes: SourceBytes);
        var sourceId = Guid.NewGuid();

        var jobId = await harness.Coordinator.EnqueueAsync(EditInput(ImageEditMode.Img2Img, sourceId, strength: 0.65), CancellationToken.None);

        var created = AssertEx.NotNull(harness.Store.Created.GetValueOrDefault(jobId));
        AssertEx.Equal(ImageEditMode.Img2Img, created.EditMode);
        AssertEx.Equal(sourceId, created.SourceImageId);
        AssertEx.Equal(0.65, created.Strength);
        await WaitForStatusAsync(harness, jobId, ImageJobStatus.Succeeded);
    }

    private static CreateImageJobInput TextInput()
    {
        return new CreateImageJobInput
        {
            ModelName = Sd15Model,
            Prompt = "a fox"
        };
    }

    private static CreateImageJobInput EditInput(ImageEditMode? mode, Guid? sourceImageId, double? strength = null)
    {
        return TextInput() with
        {
            EditMode = mode,
            SourceImageId = sourceImageId,
            Strength = strength
        };
    }

    private static async Task WaitForStatusAsync(Harness harness, Guid jobId, ImageJobStatus status)
    {
        await AssertEx.EventuallyAsync(() => harness.Store.StatusOf(jobId) == status, Timeout, $"Job {jobId} did not reach status {status}.");
    }

    private sealed record Harness : IDisposable
    {
        public required ImageJobCoordinator Coordinator { get; init; }

        public required RecordingRuntime Runtime { get; init; }

        public required RecordingJobStore Store { get; init; }

        public required IGeneratedImageStore Images { get; init; }

        public void Dispose()
        {
            Coordinator.Dispose();
        }

        public static Harness Create(bool sourceRowExists,
            byte[]? sourceBytes = null,
            bool blockFirstCall = false,
            Exception? runtimeFailure = null)
        {
            var store = new RecordingJobStore();
            var runtime = new RecordingRuntime(blockFirstCall, runtimeFailure);

            var images = Substitute.For<IGeneratedImageStore>();
            images.OpenReadAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                  .Returns(sourceBytes is null
                      ? null
                      : new GeneratedImageContent
                      {
                          Bytes = sourceBytes,
                          MimeType = "image/png",
                          Width = 64,
                          Height = 64
                      });

            var rows = Substitute.For<IGeneratedImageRowStore>();
            rows.FindAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(sourceRowExists
                    ? new GeneratedImageLocation
                    {
                        JobId = null,
                        MimeType = "image/png",
                        Width = 64,
                        Height = 64,
                        StoragePath = "unused"
                    }
                    : null);

            var registry = Substitute.For<IImageModelRegistry>();
            registry.FindAsync(Sd15Model, Arg.Any<CancellationToken>())
                    .Returns(new ImageModelRegistryEntry
                    {
                        ModelName = Sd15Model,
                        RepoId = "leejet/stable-diffusion-1.5-gguf",
                        Family = ImageModelFamily.Sd15,
                        Kind = ImageModelKind.Txt2Img,
                        Parts = [],
                        SizeBytes = 0,
                        SourceRevision = "main",
                        DownloadedAtUtc = DateTimeOffset.UnixEpoch
                    });

            var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
            supervisor.ListRunningProcesses().Returns([]);

            var services = new ServiceCollection();
            services.AddScoped<IImageJobStore>(_ => store);
            services.AddScoped(_ => rows);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            // Ownership transfers to the returned Harness, whose Dispose disposes the coordinator.
#pragma warning disable CA2000
            var coordinator = new ImageJobCoordinator(runtime,
                images,
                scopeFactory,
                new NullImageJobEventPublisher(),
                TimeProvider.System,
                NullLogger<ImageJobCoordinator>.Instance,
                new ImageRuntimeActivityGate(),
                new GpuWorkGate(),
                supervisor,
                registry);
#pragma warning restore CA2000

            return new Harness
            {
                Coordinator = coordinator,
                Runtime = runtime,
                Store = store,
                Images = images
            };
        }
    }

    /// <summary>Records every request; optionally blocks the first call until released, or fails every call.</summary>
    private sealed class RecordingRuntime : IImageRuntime
    {
        private readonly TaskCompletionSource _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _blockFirstCall;
        private readonly Exception? _failure;
        private int _callCount;

        public RecordingRuntime(bool blockFirstCall, Exception? failure)
        {
            _blockFirstCall = blockFirstCall;
            _failure = failure;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public ConcurrentQueue<ImageGenerationRequest> Requests { get; } = new();

        public Task FirstCallStarted => _firstStarted.Task;

        public void ReleaseFirstCall()
        {
            _ = _releaseFirst.TrySetResult();
        }

        public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, IProgress<ImageGenProgress> progress, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _callCount);
            Requests.Enqueue(request);
            if (_failure is not null)
            {
                throw _failure;
            }

            if (call == 1 && _blockFirstCall)
            {
                _ = _firstStarted.TrySetResult();
                await _releaseFirst.Task.WaitAsync(ct);
            }

            return new ImageGenerationResult
            {
                ImageBytes = new byte[] { 1 },
                Width = request.Width,
                Height = request.Height,
                Seed = 1
            };
        }
    }

    /// <summary>In-memory job rows: keeps every create as handed over plus the current status and failure text.</summary>
    private sealed class RecordingJobStore : IImageJobStore
    {
        private readonly ConcurrentDictionary<Guid, (ImageJobStatus Status, string? Error)> _state = new();

        public ConcurrentDictionary<Guid, ImageJobCreate> Created { get; } = new();

        public ImageJobStatus? StatusOf(Guid jobId) => _state.TryGetValue(jobId, out var state) ? state.Status : null;

        public string? FailureOf(Guid jobId) => _state.TryGetValue(jobId, out var state) ? state.Error : null;

        public Task CreateQueuedAsync(ImageJobCreate create, CancellationToken cancellationToken)
        {
            Created[create.Id] = create;
            _state[create.Id] = (ImageJobStatus.Queued, null);
            return Task.CompletedTask;
        }

        public Task<ImageJobView?> GetAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<ImageJobView?>(null);

        public Task<IReadOnlyList<ImageJobView>> ListAsync(int limit, int offset, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ImageJobView>>([]);

        public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(Created.Count);

        public Task<IReadOnlyList<string>?> DeleteAsync(Guid jobId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>?>(null);

        public Task MarkGeneratingAsync(Guid jobId, long startedAtUtc, CancellationToken cancellationToken) => Set(jobId, ImageJobStatus.Generating, error: null);

        public Task MarkSucceededAsync(Guid jobId,
            Guid imageId,
            long completedAtUtc,
            long durationMs,
            int outputWidth,
            int outputHeight,
            long resolvedSeed,
            CancellationToken cancellationToken) => Set(jobId, ImageJobStatus.Succeeded, error: null);

        public Task MarkFailedAsync(Guid jobId, string sanitizedError, long completedAtUtc, CancellationToken cancellationToken) =>
            Set(jobId, ImageJobStatus.Failed, sanitizedError);

        public Task MarkCancelledAsync(Guid jobId, long completedAtUtc, CancellationToken cancellationToken) => Set(jobId, ImageJobStatus.Cancelled, error: null);

        public Task MarkCancellationRequestedAsync(Guid jobId, long requestedAtUtc, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<Guid>> MarkInterruptedFailedAsync(string sanitizedError, long completedAtUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        private Task Set(Guid jobId, ImageJobStatus status, string? error)
        {
            _state[jobId] = (status, error);
            return Task.CompletedTask;
        }
    }
}
