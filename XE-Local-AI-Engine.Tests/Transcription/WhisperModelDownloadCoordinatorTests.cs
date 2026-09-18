namespace XE_Local_AI_Engine.Tests.Transcription;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The coordinator's job is that a detached download is still observable: it is single-flight per model, it always
///     lands in a terminal phase, and a late progress tick can never resurrect a finished one.
/// </summary>
/// <remarks>
///     The weight store is a hand-written fake rather than a substitute because the behaviour under test is ORDERING
///     and cancellation across two calls, which <c>Returns</c>/<c>Received</c> cannot express without becoming harder
///     to read than the fake.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class WhisperModelDownloadCoordinatorTests
{
    [Test]
    public async Task Start_SameModelTwice_RejoinsInsteadOfStartingASecondTransfer()
    {
        var store = new FakeWhisperWeightFileStore
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var fixture = new Fixture(store);

        var first = fixture.Coordinator.Start("base");
        await store.FirstCallReached.Task;
        var second = fixture.Coordinator.Start("base");

        AssertEx.False(first.AlreadyInFlight);
        AssertEx.True(second.AlreadyInFlight, "A double submit must rejoin rather than start a second transfer.");

        store.Gate.SetResult();
        await AssertEx.EventuallyAsync(() => fixture.Phase("base") == WhisperModelDownloadPhase.Completed, TestBudgets.Contended);
        AssertEx.Equal(expected: 2, store.Requests.Count, "Exactly one download of two parts must have run.");
    }

    [Test]
    public async Task Start_FetchesTheVadFileBeforeTheWeight()
    {
        // Every launch runs with voice-activity detection on, so a weight without its VAD model is an installation
        // that cannot serve a request.
        var store = new FakeWhisperWeightFileStore();
        using var fixture = new Fixture(store);

        fixture.Coordinator.Start("base");
        await AssertEx.EventuallyAsync(() => fixture.Phase("base") == WhisperModelDownloadPhase.Completed, TestBudgets.Contended);

        AssertEx.Equal(expected: 2, store.Requests.Count);
        AssertEx.Equal(WhisperModelCatalog.VadFileName, store.Requests[0].FileName);
        AssertEx.Equal("ggml-base.bin", store.Requests[1].FileName);
    }

    [Test]
    public async Task Start_PassesTheCataloguesOwnSizeAndDigest()
    {
        // The expected size and digest come from the pinned catalogue, never from what the repository says about
        // itself, which is what makes the verification meaningful.
        var store = new FakeWhisperWeightFileStore();
        using var fixture = new Fixture(store);
        var entry = AssertEx.NotNull(WhisperModelCatalog.Find("base"));

        fixture.Coordinator.Start("base");
        await AssertEx.EventuallyAsync(() => fixture.Phase("base") == WhisperModelDownloadPhase.Completed, TestBudgets.Contended);

        AssertEx.Equal(WhisperModelCatalog.VadSha256, store.Requests[0].ExpectedSha256);
        AssertEx.Equal(WhisperModelCatalog.VadSizeBytes, store.Requests[0].ExpectedSizeBytes);
        AssertEx.Equal(entry.Sha256, store.Requests[1].ExpectedSha256);
        AssertEx.Equal(entry.SizeBytes, store.Requests[1].ExpectedSizeBytes);
    }

    [Test]
    public async Task Run_StoreThrows_LandsInFailedWithASanitizedReason()
    {
        var store = new FakeWhisperWeightFileStore
        {
            Failure = new HuggingFaceDownloadException(HuggingFaceDownloadFailure.NotFound,
                "The requested transcription model was not found.")
        };
        using var fixture = new Fixture(store);

        fixture.Coordinator.Start("base");

        await AssertEx.EventuallyAsync(() => fixture.Phase("base") == WhisperModelDownloadPhase.Failed, TestBudgets.Contended,
            "A failed download must land in an observable terminal phase, not vanish into a log line.");
        var status = AssertEx.NotNull(fixture.Coordinator.GetStatus("base"));
        AssertEx.Equal("The requested transcription model was not found.", AssertEx.NotNull(status.SanitizedError));
    }

    [Test]
    public async Task Run_UnexpectedTransportFailure_ReportsAGenericReason()
    {
        // A raw transport message can carry a URL or a path, so it is collapsed rather than surfaced.
        var store = new FakeWhisperWeightFileStore
        {
            Failure = new HttpRequestException("GET https://huggingface.co/secret/path failed")
        };
        using var fixture = new Fixture(store);

        fixture.Coordinator.Start("base");

        await AssertEx.EventuallyAsync(() => fixture.Phase("base") == WhisperModelDownloadPhase.Failed, TestBudgets.Contended);
        var status = AssertEx.NotNull(fixture.Coordinator.GetStatus("base"));
        AssertEx.Equal("Download failed.", AssertEx.NotNull(status.SanitizedError));
    }

    [Test]
    public async Task Cancel_InFlight_LandsInCancelled()
    {
        var store = new FakeWhisperWeightFileStore
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var fixture = new Fixture(store);

        fixture.Coordinator.Start("base");
        await store.FirstCallReached.Task;

        AssertEx.True(fixture.Coordinator.Cancel("base"));

        await AssertEx.EventuallyAsync(() => fixture.Phase("base") == WhisperModelDownloadPhase.Cancelled, TestBudgets.Contended);
    }

    [Test]
    public async Task Cancel_NothingInFlight_ReportsFalse()
    {
        var store = new FakeWhisperWeightFileStore();
        using var fixture = new Fixture(store);

        AssertEx.False(fixture.Coordinator.Cancel("base"), "Cancelling a download that was never started is not an error.");

        fixture.Coordinator.Start("base");
        await AssertEx.EventuallyAsync(() => fixture.Phase("base") == WhisperModelDownloadPhase.Completed, TestBudgets.Contended);

        AssertEx.False(fixture.Coordinator.Cancel("base"), "Cancelling a finished download is idempotent, not an error.");
    }

    [Test]
    public async Task Progress_AfterTerminal_DoesNotResurrectRunning()
    {
        // Progress marshals through the captured context, so a tick queued just before completion can be delivered
        // after the terminal write. Publishing it unguarded would hang the UI on a download that is already over.
        var store = new FakeWhisperWeightFileStore();
        using var fixture = new Fixture(store);

        fixture.Coordinator.Start("base");
        await AssertEx.EventuallyAsync(() => fixture.Phase("base") == WhisperModelDownloadPhase.Completed, TestBudgets.Contended);

        store.ReplayLastProgress();
        await AssertEx.SettleAsync();

        AssertEx.Equal(WhisperModelDownloadPhase.Completed, fixture.Phase("base"),
            "A late progress tick must never move a finished download back to Running.");
    }

    [Test]
    public void Start_UnknownModelId_Throws()
    {
        var store = new FakeWhisperWeightFileStore();
        using var fixture = new Fixture(store);

        AssertEx.Throws<ArgumentException>(() => fixture.Coordinator.Start("not-a-model"));
        AssertEx.Empty(store.Requests, "An unknown id must be rejected before anything is fetched.");
    }

    [Test]
    public async Task ListStatuses_ReportsEveryTrackedDownload()
    {
        var store = new FakeWhisperWeightFileStore();
        using var fixture = new Fixture(store);

        fixture.Coordinator.Start("base");
        await AssertEx.EventuallyAsync(() => fixture.Phase("base") == WhisperModelDownloadPhase.Completed, TestBudgets.Contended);
        fixture.Coordinator.Start("small");
        await AssertEx.EventuallyAsync(() => fixture.Phase("small") == WhisperModelDownloadPhase.Completed, TestBudgets.Contended);

        var statuses = fixture.Coordinator.ListStatuses();

        AssertEx.Equal(expected: 2, statuses.Count);
        AssertEx.Contains(statuses, status => status.ModelId == "base");
        AssertEx.Contains(statuses, status => status.ModelId == "small");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        public Fixture(IWhisperWeightFileStore store)
        {
            _root = Path.Combine(Path.GetTempPath(), "xe-whisper-dl-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            var resolver = new WhisperModelPathResolver(new FakeNodeDataDirectory(_root));
            Coordinator = new WhisperModelDownloadCoordinator(store, resolver, NullLogger<WhisperModelDownloadCoordinator>.Instance);
        }

        public WhisperModelDownloadCoordinator Coordinator { get; }

        public WhisperModelDownloadPhase? Phase(string modelId) =>
            Coordinator.GetStatus(modelId)?.Phase;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }

        private sealed class FakeNodeDataDirectory : INodeDataDirectory
        {
            public FakeNodeDataDirectory(string root)
            {
                Root = root;
            }

            public string Root { get; }
        }
    }

    private sealed class FakeWhisperWeightFileStore : IWhisperWeightFileStore
    {
        private IProgress<PullProgress>? _lastProgress;
        private string? _lastModelId;

        public List<WhisperWeightFileRequest> Requests { get; } = [];

        /// <summary>When set, the FIRST call parks here so a test can act while a download is genuinely in flight.</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>Signalled once the first call has been entered, so a test never races the code under test.</summary>
        public TaskCompletionSource FirstCallReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When set, every call throws it.</summary>
        public Exception? Failure { get; set; }

        public async Task<string> EnsureFileAsync(WhisperWeightFileRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
        {
            var isFirst = Requests.Count == 0;
            Requests.Add(request);
            _lastProgress = progress;
            _lastModelId = request.ProgressLabel;

            if (isFirst)
            {
                FirstCallReached.TrySetResult();
                if (Gate is { } gate)
                {
                    await gate.Task.WaitAsync(ct);
                }
            }

            ct.ThrowIfCancellationRequested();

            if (Failure is { } failure)
            {
                throw failure;
            }

            progress?.Report(new PullProgress
            {
                ModelName = request.ProgressLabel,
                Status = "downloading",
                TotalBytes = request.ExpectedSizeBytes,
                CompletedBytes = request.ExpectedSizeBytes
            });

            return request.DestinationPath;
        }

        /// <summary>Re-reports the last progress tick, simulating one delivered after the terminal write.</summary>
        public void ReplayLastProgress()
        {
            _lastProgress?.Report(new PullProgress
            {
                ModelName = _lastModelId ?? "base",
                Status = "downloading",
                TotalBytes = 1,
                CompletedBytes = 1
            });
        }
    }
}
