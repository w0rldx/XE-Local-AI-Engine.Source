namespace XE_Local_AI_Engine.Tests.Images;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Images.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     F-031: a failed image-model weight download used to be swallowed into a log line, leaving the operator staring at
///     an optimistic "download started" toast forever. These tests pin the contract that every download reaches an
///     observable terminal phase — and that a failure is reported as <c>Failed</c> with an operator-safe reason rather
///     than staying <c>Running</c> indefinitely.
/// </summary>
public sealed class ImageModelDownloadCoordinatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static ImageModelRequest Request(string modelName = "bogus-model")
    {
        return new ImageModelRequest
        {
            ModelName = modelName,
            RepoId = "Comfy-Org/stable-diffusion-v1-5-archive",
            Family = ImageModelFamily.Sd15,
            Parts = [new ImageModelPartRequest { Role = ImageModelPartRole.Diffusion, FileName = "this-file-does-not-exist.safetensors" }]
        };
    }

    private static ImageModelDownloadCoordinator Coordinator(IImageModelStore store)
    {
        return new ImageModelDownloadCoordinator(store, NullLogger<ImageModelDownloadCoordinator>.Instance);
    }

    private static async Task<ImageModelDownloadStatus> WaitForTerminalAsync(IImageModelDownloadCoordinator coordinator, string modelName)
    {
        await AssertEx.EventuallyAsync(() => coordinator.GetStatus(modelName) is { Phase: not ImageModelDownloadPhase.Running },
                           Timeout,
                           $"The download for {modelName} never reached a terminal phase.")
                      .ConfigureAwait(false);

        return AssertEx.NotNull(coordinator.GetStatus(modelName));
    }

    [Test]
    public async Task Start_WhenTheWeightFileDoesNotExist_ReachesFailedWithASanitizedReason()
    {
        const string sanitized = "The requested file was not found in the repository.";
        var store = new ThrowingImageModelStore(new HuggingFaceDownloadException(HuggingFaceDownloadFailure.NotFound, sanitized));
        var coordinator = Coordinator(store);

        var ticket = coordinator.Start(Request());

        var status = await WaitForTerminalAsync(coordinator, ticket.ModelName).ConfigureAwait(false);
        AssertEx.Equal(ImageModelDownloadPhase.Failed, status.Phase, "A download that cannot find its file must land in Failed, not hang in Running.");
        AssertEx.Equal(sanitized, status.SanitizedError);
    }

    [Test]
    public async Task Start_WhenTheTransportFails_ReportsFailedWithoutLeakingTheRawMessage()
    {
        var store = new ThrowingImageModelStore(new HttpRequestException("connection to https://internal.host/models/secret-path refused"));
        var coordinator = Coordinator(store);

        var ticket = coordinator.Start(Request());

        var status = await WaitForTerminalAsync(coordinator, ticket.ModelName).ConfigureAwait(false);
        AssertEx.Equal(ImageModelDownloadPhase.Failed, status.Phase);
        var reason = AssertEx.NotNull(status.SanitizedError);
        AssertEx.False(reason.Contains("internal.host", StringComparison.Ordinal), "A transport failure must not surface the raw URL.");
    }

    [Test]
    public async Task Start_WhenTheDownloadSucceeds_ReachesCompletedAndIsListed()
    {
        var coordinator = Coordinator(new CompletingImageModelStore());

        var ticket = coordinator.Start(Request("sd-1.5-fp16"));

        var status = await WaitForTerminalAsync(coordinator, ticket.ModelName).ConfigureAwait(false);
        AssertEx.Equal(ImageModelDownloadPhase.Completed, status.Phase);
        AssertEx.Null(status.SanitizedError);
        AssertEx.Contains(coordinator.ListStatuses(), entry => entry.ModelName == "sd-1.5-fp16");
    }

    [Test]
    public async Task Start_WhenAlreadyInFlight_RejoinsInsteadOfStartingASecondTransfer()
    {
        var store = new BlockingImageModelStore();
        var coordinator = Coordinator(store);

        var first = coordinator.Start(Request("sd-1.5-fp16"));
        var second = coordinator.Start(Request("sd-1.5-fp16"));

        AssertEx.False(first.AlreadyInFlight);
        AssertEx.True(second.AlreadyInFlight, "A double submit must rejoin the in-flight download, not start a duplicate.");

        store.Release();
        _ = await WaitForTerminalAsync(coordinator, "sd-1.5-fp16").ConfigureAwait(false);
        AssertEx.Equal(expected: 1, store.CallCount, "Only one transfer may have been started.");
    }

    /// <summary>Base store double: every member throws unless the test needs it; only EnsureModelAsync is exercised.</summary>
    private abstract class StubImageModelStore : IImageModelStore
    {
        public Task<IReadOnlyList<ImageModelPart>?> ResolveModelPartsAsync(string modelName, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public abstract Task<ImageModelHandle> EnsureModelAsync(ImageModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct);

        public Task DeleteModelAsync(string modelName, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task<bool> ExistsAsync(string modelName, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        protected static ImageModelHandle Handle(ImageModelRequest request)
        {
            return new ImageModelHandle(request.ModelName, request.Family, request.Kind, []);
        }
    }

    private sealed class ThrowingImageModelStore(Exception failure) : StubImageModelStore
    {
        public override Task<ImageModelHandle> EnsureModelAsync(ImageModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
        {
            return Task.FromException<ImageModelHandle>(failure);
        }
    }

    private sealed class CompletingImageModelStore : StubImageModelStore
    {
        public override Task<ImageModelHandle> EnsureModelAsync(ImageModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
        {
            progress?.Report(new PullProgress
            {
                ModelName = request.ModelName,
                Status = "downloading",
                TotalBytes = 2_000,
                CompletedBytes = 2_000
            });
            return Task.FromResult(Handle(request));
        }
    }

    private sealed class BlockingImageModelStore : StubImageModelStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public void Release()
        {
            _ = _release.TrySetResult();
        }

        public override async Task<ImageModelHandle> EnsureModelAsync(ImageModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
        {
            _ = Interlocked.Increment(ref _callCount);
            await _release.Task.ConfigureAwait(false);
            return Handle(request);
        }
    }
}
