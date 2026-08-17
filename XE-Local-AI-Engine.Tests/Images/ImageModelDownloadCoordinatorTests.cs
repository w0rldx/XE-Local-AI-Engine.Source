namespace XE_Local_AI_Engine.Tests.Images;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Images.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A failed image-model weight download used to be swallowed into a log line, leaving the operator staring at
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
            Parts =
            [
                new ImageModelPartRequest
                {
                    Role = ImageModelPartRole.Diffusion,
                    FileName = "this-file-does-not-exist.safetensors"
                }
            ]
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

    [Test]
    public async Task Cancel_WhenTheDownloadIsInFlight_StopsItAndLandsInTheCancelledPhase()
    {
        // An image file-set can be tens of gigabytes, so a mis-started download that could not be stopped would hold the
        // node's bandwidth and disk until it finished. Cancelling must both signal the transfer and reach a terminal
        // phase the UI can act on — a cancel that leaves the row stuck on "Running" is indistinguishable from no cancel.
        var store = new CancellableImageModelStore();
        var coordinator = Coordinator(store);

        var ticket = coordinator.Start(Request("sd-1.5-fp16"));
        await AssertEx.EventuallyAsync(() => store.IsRunning, Timeout, "The download never started.").ConfigureAwait(false);

        AssertEx.True(coordinator.Cancel(ticket.ModelName), "Cancelling an in-flight download must report that it was signalled.");

        var status = await WaitForTerminalAsync(coordinator, ticket.ModelName).ConfigureAwait(false);
        AssertEx.Equal(ImageModelDownloadPhase.Cancelled, status.Phase, "A cancelled download must land in Cancelled, not Failed or Running.");
        AssertEx.True(store.SawCancellation, "The cancellation token handed to the store must be the one Cancel() signals.");
    }

    [Test]
    public void Cancel_WhenNothingIsInFlight_ReportsFalseWithoutThrowing()
    {
        var coordinator = Coordinator(new CompletingImageModelStore());

        AssertEx.False(coordinator.Cancel("never-started"), "Cancelling an unknown download is a no-op, not an error.");
    }

    [Test]
    public async Task Cancel_WhenTheDownloadAlreadyFinished_ReportsFalse()
    {
        // The operator clicked Cancel on a row that had just completed. That is a race, not a mistake — it must not
        // throw (the source is disposed by then) and must not claim to have stopped anything.
        var coordinator = Coordinator(new CompletingImageModelStore());

        var ticket = coordinator.Start(Request("sd-1.5-fp16"));
        _ = await WaitForTerminalAsync(coordinator, ticket.ModelName).ConfigureAwait(false);

        AssertEx.False(coordinator.Cancel(ticket.ModelName), "A finished download has nothing left to cancel.");
    }

    [Test]
    public async Task Cancel_CalledTwice_IsIdempotent()
    {
        var store = new CancellableImageModelStore();
        var coordinator = Coordinator(store);

        var ticket = coordinator.Start(Request("sd-1.5-fp16"));
        await AssertEx.EventuallyAsync(() => store.IsRunning, Timeout, "The download never started.").ConfigureAwait(false);

        AssertEx.True(coordinator.Cancel(ticket.ModelName));
        // The second call may land before or after the run removed its entry; either answer is fine, but it must never
        // throw on the already-cancelled (or already-disposed) source.
        _ = coordinator.Cancel(ticket.ModelName);

        var status = await WaitForTerminalAsync(coordinator, ticket.ModelName).ConfigureAwait(false);
        AssertEx.Equal(ImageModelDownloadPhase.Cancelled, status.Phase);
        AssertEx.False(coordinator.Cancel(ticket.ModelName), "Once the run has finished, a further cancel reports nothing to stop.");
    }

    [Test]
    public async Task Start_WhenTheStoreReportsPartProgress_CarriesThePartFramingIntoTheStatus()
    {
        // An image model is a file SET. Without the part framing the UI cannot tell "the bar restarted because part 2
        // began" from "the bar restarted because something went wrong".
        var store = new PartReportingImageModelStore();
        var coordinator = Coordinator(store);

        var ticket = coordinator.Start(Request("qwen-image"));

        await AssertEx.EventuallyAsync(() => coordinator.GetStatus(ticket.ModelName)?.PartIndex == 2,
                          Timeout,
                          "The part index never reached the status registry.")
                      .ConfigureAwait(false);

        var status = AssertEx.NotNull(coordinator.GetStatus(ticket.ModelName));
        AssertEx.Equal(expected: 2, status.PartIndex ?? 0);
        AssertEx.Equal(expected: 3, status.PartCount ?? 0);

        store.Release();
        _ = await WaitForTerminalAsync(coordinator, ticket.ModelName).ConfigureAwait(false);
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

    // Runs until its cancellation token fires, recording that it saw the cancel — so a test can prove the token the
    // coordinator handed the store is the one Cancel() signals, not merely that the phase changed.
    private sealed class CancellableImageModelStore : StubImageModelStore
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sawCancellation;

        public bool IsRunning => _started.Task.IsCompleted;

        public bool SawCancellation => Volatile.Read(ref _sawCancellation) == 1;

        public override async Task<ImageModelHandle> EnsureModelAsync(ImageModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
        {
            _ = _started.TrySetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = Interlocked.Exchange(ref _sawCancellation, value: 1);
                throw;
            }

            return Handle(request);
        }
    }

    // Reports one mid-set progress tick (part 2 of 3) and then blocks, so the test observes the Running status while it
    // is still Running — a store that completed immediately would overwrite it with the terminal status first.
    private sealed class PartReportingImageModelStore : StubImageModelStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release()
        {
            _ = _release.TrySetResult();
        }

        public override async Task<ImageModelHandle> EnsureModelAsync(ImageModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
        {
            progress?.Report(new PullProgress
            {
                ModelName = request.ModelName,
                Status = "downloading",
                TotalBytes = 300,
                CompletedBytes = 150,
                PartIndex = 2,
                PartCount = 3
            });

            await _release.Task.ConfigureAwait(false);
            return Handle(request);
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
