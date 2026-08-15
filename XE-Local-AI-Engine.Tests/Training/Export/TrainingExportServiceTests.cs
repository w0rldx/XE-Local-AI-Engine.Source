namespace XE_Local_AI_Engine.Tests.Training.Export;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training;
using XE_Local_AI_Engine.Client.Services.Training.Export;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Training.Runs;

/// <summary>
///     The export pipeline against scripted subprocesses: no venv, no llama.cpp, no GPU. What is pinned here is the
///     part a live export cannot check cheaply — that a failure is recorded ON the artifact and leaves the finished
///     run alone, that a runtime with no quantizer says so specifically instead of failing at the subprocess, and
///     that an architecture this engine cannot serve is rejected before the smoke gate ever runs.
/// </summary>
public sealed class TrainingExportServiceTests : IDisposable
{
    private readonly List<FixedNodeSqliteKeyHolder> _keyHolders = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var keyHolder in _keyHolders)
        {
            keyHolder.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Export_MergedHappyPath_StagesTheQuantizedFileAndPassesSmoke()
    {
        using var harness = Harness.Create(this);
        harness.ScriptMergedPipeline();

        var start = await harness.StartAsync(TrainingArtifactKind.MergedGguf);

        AssertEx.Equal(TrainingExportStartOutcome.Accepted, start.Outcome);
        await harness.DrainAsync();
        AssertEx.Equal(3, harness.Spawner.Requests.Count);
        // convert_hf_to_gguf.py and llama-quantize both need the vendored gguf-py on PYTHONPATH; the merge step
        // deliberately does not get it.
        AssertEx.Null(harness.Spawner.Requests[0].GgufPyDirectory);
        AssertEx.Equal(Harness.GgufPyDirectory, harness.Spawner.Requests[1].GgufPyDirectory);
        AssertEx.Equal(TrainingArtifactSmokeState.Passed, harness.RecordedSmokeState);
    }

    [Test]
    public async Task Export_MergedWithoutAQuantizer_FailsWithTheRuntimeSpecificReason()
    {
        // Every upstream prebuilt archive ships llama-server without llama-quantize, so this is the DEFAULT install,
        // not an edge case — the message has to name the fix rather than surface a missing-file error.
        using var harness = Harness.Create(this, quantizerPresent: false);
        harness.ScriptMergedPipeline();

        _ = await harness.StartAsync(TrainingArtifactKind.MergedGguf);
        await harness.DrainAsync();

        AssertEx.Equal(TrainingArtifactSmokeState.Failed, harness.RecordedSmokeState);
        AssertEx.Contains(harness.RecordedSmokeReason ?? string.Empty, "llama-quantize", StringComparison.Ordinal);
    }

    [Test]
    public async Task Export_WhenTheMergeFails_RecordsTheReasonOnTheArtifactAndLeavesTheRunSucceeded()
    {
        using var harness = Harness.Create(this);
        _ = harness.Spawner.Then(exitCode: 1, effect: null, """{"event":"error","category":"OutOfMemoryError","message":"CUDA out of memory"}""");

        _ = await harness.StartAsync(TrainingArtifactKind.MergedGguf);
        await harness.DrainAsync();

        AssertEx.Equal(TrainingArtifactSmokeState.Failed, harness.RecordedSmokeState);
        AssertEx.Contains(harness.RecordedSmokeReason ?? string.Empty, "CUDA out of memory", StringComparison.Ordinal);

        // The training itself succeeded. An export is a separate, retryable step and must never be able to rewrite
        // that verdict — the store could not even express it, and pretending otherwise would lose the adapter.
        _ = await harness.Store.DidNotReceiveWithAnyArgs().CompleteRunAsync(Guid.Empty, default, default, default);
        _ = await harness.Store.DidNotReceiveWithAnyArgs().TransitionAsync(Guid.Empty, default, default, default);
    }

    [Test]
    public async Task Export_WhenTheArchitectureIsNotSupported_SkipsSmokeWithAVisibleReason()
    {
        using var harness = Harness.Create(this,
            inspection: new GgufImportInspection(SizeBytes: 4,
                GgufVersion: 3,
                "mamba",
                Workload: null,
                "Q4_K_M",
                "merged-Q4_K_M.gguf",
                [GgufImportRejectionCode.UnsupportedArchitecture],
                []));
        harness.ScriptMergedPipeline();

        _ = await harness.StartAsync(TrainingArtifactKind.MergedGguf);
        await harness.DrainAsync();

        AssertEx.Equal(TrainingArtifactSmokeState.Skipped, harness.RecordedSmokeState);
        AssertEx.Contains(harness.RecordedSmokeReason ?? string.Empty, "mamba", StringComparison.Ordinal);
        // Nothing is loaded for an artifact that could never be committed: the smoke gate's error would say nothing
        // useful, and the operator would be left guessing.
        AssertEx.False(harness.SmokeRan, "An unsupported architecture must be rejected before the smoke load.");
    }

    [Test]
    public async Task Export_AdapterMode_ConvertsWithoutAMergeStep()
    {
        using var harness = Harness.Create(this);
        _ = harness.Spawner.Then(exitCode: 0, request => File.WriteAllText(request.Arguments[6], "gguf"));

        _ = await harness.StartAsync(TrainingArtifactKind.AdapterGguf);
        await harness.DrainAsync();

        AssertEx.Equal(1, harness.Spawner.Requests.Count);
        var arguments = harness.Spawner.Requests[0].Arguments;
        AssertEx.Contains(arguments[0], "convert_lora_to_gguf.py", StringComparison.Ordinal);
        AssertEx.Equal("--base", arguments[1]);
        AssertEx.Equal(TrainingArtifactSmokeState.Passed, harness.RecordedSmokeState);
    }

    [Test]
    public async Task Start_WhileTrainingHoldsTheGpu_IsRefusedBeforeAnythingIsWritten()
    {
        using var harness = Harness.Create(this);
        using var held = AssertEx.NotNull(harness.Activity.TryBegin(), "The flag must be free to begin with.");

        var start = await harness.StartAsync(TrainingArtifactKind.MergedGguf);

        AssertEx.Equal(TrainingExportStartOutcome.Busy, start.Outcome);
        AssertEx.Equal(0, harness.Spawner.Requests.Count);
        _ = await harness.Store.DidNotReceiveWithAnyArgs().CreateArtifactAsync(default!, default);
    }

    [Test]
    public async Task Start_WithAnUnsupportedQuantization_IsRejected()
    {
        using var harness = Harness.Create(this);

        var start = await harness.StartAsync(TrainingArtifactKind.MergedGguf, quantType: "IQ1_S");

        AssertEx.Equal(TrainingExportStartOutcome.UnsupportedQuantization, start.Outcome);
    }

    [Test]
    public async Task Start_ForARunThatNeverFinished_IsRefused()
    {
        using var harness = Harness.Create(this, runStatus: TrainingRunStatus.Failed);

        var start = await harness.StartAsync(TrainingArtifactKind.MergedGguf);

        AssertEx.Equal(TrainingExportStartOutcome.RunNotExportable, start.Outcome);
    }

    /// <summary>Wires one export service over scripted collaborators and a temp-directory workspace.</summary>
    private sealed class Harness : IDisposable
    {
        public const string GgufPyDirectory = "/opt/llama.cpp/gguf-py";

        private readonly ServiceProvider _provider;
        private readonly TrainingExportService _service;
        private readonly string _stagedDirectory;

        private Harness(ServiceProvider provider,
            TrainingExportService service,
            ITrainingRunStore store,
            ScriptedExportSpawner spawner,
            ITrainingActivity activity,
            Guid runId,
            string stagedDirectory)
        {
            _provider = provider;
            _service = service;
            Store = store;
            Spawner = spawner;
            Activity = activity;
            RunId = runId;
            _stagedDirectory = stagedDirectory;
        }

        public ITrainingRunStore Store { get; }
        public ScriptedExportSpawner Spawner { get; }
        public ITrainingActivity Activity { get; }
        public Guid RunId { get; }
        public TrainingArtifactSmokeState? RecordedSmokeState { get; private set; }
        public string? RecordedSmokeReason { get; private set; }
        public bool SmokeRan { get; private set; }

        public static Harness Create(TrainingExportServiceTests owner,
            bool quantizerPresent = true,
            TrainingRunStatus runStatus = TrainingRunStatus.Succeeded,
            GgufImportInspection? inspection = null)
        {
            var runId = Guid.NewGuid();
            var dataDirectory = new FixedNodeDataDirectory(owner._root);
            // Owned by the test class so the harness never has to: nothing here encrypts anything, but the workspace
            // takes a real holder.
            var keyHolder = new FixedNodeSqliteKeyHolder(new byte[32]);
            owner._keyHolders.Add(keyHolder);
            var workspace = new TrainingRunWorkspace(dataDirectory, keyHolder);
            var staged = workspace.StagedDirectory(runId);
            var adapterDirectory = Path.Combine(staged, "adapter");
            _ = Directory.CreateDirectory(adapterDirectory);

            var store = Substitute.For<ITrainingRunStore>();
            var harnessBox = new Harness[1];
            ConfigureStore(store, runId, runStatus, adapterDirectory, harnessBox);

            var runtime = Substitute.For<ITrainingRuntimeService>();
            _ = runtime.ResolveInterpreterPath().Returns("/venv/bin/python");
            _ = runtime.GetStatus()
                       .Returns(new TrainingRuntimeStatus(TrainingRuntimePhase.Ready, IsRunning: false, Terminal: true, [],
                           LogStartSequence: 0, SanitizedError: null, Installed: null, StartedAtUtc: null, CompletedAtUtc: null));

            var convertScripts = Substitute.For<IConvertScriptProvisioner>();
            _ = convertScripts.EnsureAsync(Arg.Any<CancellationToken>())
                              .Returns(new ConvertScriptPaths("/opt/llama.cpp/convert_hf_to_gguf.py",
                                  "/opt/llama.cpp/convert_lora_to_gguf.py",
                                  GgufPyDirectory,
                                  "abc123"));

            // The quantizer is located by NAME beside the resolved server, so its presence is modelled the way the
            // product resolves it: a real sibling file, or the absence of one.
            var binDirectory = Path.Combine(owner._root, "llamacpp", "bin");
            _ = Directory.CreateDirectory(binDirectory);
            var serverPath = Path.Combine(binDirectory, "llama-server");
            File.WriteAllText(serverPath, "server");
            if (quantizerPresent)
            {
                File.WriteAllText(Path.Combine(binDirectory, LlamaCppToolBinaries.QuantizerFileName), "quantize");
            }

            var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
            _ = binaryManager.EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                             .Returns(new LlamaBinary(serverPath, "b10201", GpuVariant.Cuda, IsPinnedFallback: true));
            var variantSelector = Substitute.For<IGpuVariantSelector>();
            _ = variantSelector.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(GpuVariant.Cuda);

            var inspector = Substitute.For<IGgufImportInspector>();
            _ = inspector.InspectAsync(Arg.Any<GgufImportSource>(), Arg.Any<GgufImportInspectionMode>(), Arg.Any<CancellationToken>())
                         .Returns(callInfo => Task.FromResult(inspection ?? Accepted(callInfo.Arg<GgufImportSource>())));

            var smokeGate = Substitute.For<ITrainedModelSmokeGate>();
            _ = smokeGate.RunAsync(Arg.Any<TrainingArtifactRecordView>(), Arg.Any<CancellationToken>())
                         .Returns(_ =>
                         {
                             harnessBox[0].SmokeRan = true;
                             return Task.FromResult(new TrainedModelSmokeResult(TrainingArtifactSmokeState.Passed, Reason: null));
                         });

            var models = Substitute.For<IGgufModelStore>();
            _ = models.ResolveModelFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("/models/base.gguf");

            var services = new ServiceCollection();
            _ = services.AddSingleton(store).AddSingleton(models);
            var provider = services.BuildServiceProvider();

            // The substitute returns null for the lease by default, which reads as "a model is loaded"; a free
            // runtime hands one out.
            var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
            _ = supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>())
                          .Returns(Substitute.For<ILlamaServerRuntimeMutationLease>());

            var activity = new TrainingActivity();
            var spawner = new ScriptedExportSpawner();
            var service = new TrainingExportService(provider.GetRequiredService<IServiceScopeFactory>(),
                new TrainingRunEventBuffer(Options.Create(new TrainingRunEventBufferOptions())),
                activity,
                supervisor,
                runtime,
                spawner,
                convertScripts,
                binaryManager,
                variantSelector,
                inspector,
                smokeGate,
                workspace,
                dataDirectory,
                NullLogger<TrainingExportService>.Instance);

            var harness = new Harness(provider, service, store, spawner, activity, runId, staged);
            harnessBox[0] = harness;
            return harness;
        }

        public Task<TrainingExportStart> StartAsync(TrainingArtifactKind kind, string? quantType = null) =>
            _service.StartExportAsync(RunId, new TrainingExportRequest(kind, quantType));

        /// <summary>Awaits the detached pipeline the start returned from, so assertions never race it.</summary>
        public async Task DrainAsync()
        {
            if (_service.InFlight is { } inFlight)
            {
                await inFlight;
            }
        }

        /// <summary>merge → convert → quantize, each writing the file the next step expects to find.</summary>
        public void ScriptMergedPipeline()
        {
            _ = Spawner
                .Then(exitCode: 0, _ => Directory.CreateDirectory(Path.Combine(_stagedDirectory, "merged-hf")))
                .Then(exitCode: 0, request => File.WriteAllText(request.Arguments[4], "f16"))
                .Then(exitCode: 0, request => File.WriteAllText(request.Arguments[1], "quantized"));
        }

        public void Dispose() =>
            _provider.Dispose();

        private static GgufImportInspection Accepted(GgufImportSource source) =>
            new(SizeBytes: 4,
                GgufVersion: 3,
                "llama",
                Path.GetFileName(source.AbsolutePath).StartsWith("adapter", StringComparison.Ordinal)
                    ? GgufImportWorkload.LoraAdapter
                    : GgufImportWorkload.CausalChat,
                "Q4_K_M",
                Path.GetFileName(source.AbsolutePath),
                [],
                []);

        private static void ConfigureStore(ITrainingRunStore store,
            Guid runId,
            TrainingRunStatus runStatus,
            string adapterDirectory,
            Harness[] harnessBox)
        {
            var run = new TrainingRunRecord(runId,
                Guid.NewGuid(),
                "v1:abc",
                DatasetRevision: 1,
                FreezeJson: ReadOnlyMemory<byte>.Empty,
                BaseArtifactId: Guid.NewGuid(),
                LinkedInstalledModelName: "base:Q4_K_M",
                LinkedModelContentFingerprint: "v1:def",
                OptionsJson: ReadOnlyMemory<byte>.Empty,
                LicenseConfirmationJson: null,
                runStatus,
                ProgressJson: null,
                LogTail: null,
                LaunchReceiptJson: null,
                ErrorMessage: null,
                Version: 4,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0,
                TrainingWorkStatus.Succeeded,
                WorkErrorMessage: null);
            _ = store.GetAsync(runId, Arg.Any<CancellationToken>()).Returns(run);
            _ = store.ListArtifactsAsync(runId, Arg.Any<CancellationToken>())
                     .Returns<IReadOnlyList<TrainingArtifactRecord>>(
                     [
                         Artifact(runId, TrainingArtifactKind.HfAdapterDir, adapterDirectory, TrainingArtifactSmokeState.Pending)
                     ]);
            _ = store.CreateArtifactAsync(Arg.Any<TrainingArtifactInput>(), Arg.Any<CancellationToken>())
                     .Returns(callInfo => Task.FromResult(Artifact(runId,
                         callInfo.Arg<TrainingArtifactInput>().Kind,
                         callInfo.Arg<TrainingArtifactInput>().Path,
                         TrainingArtifactSmokeState.Pending)));
            _ = store.SetArtifactDigestAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                     .Returns(callInfo => Task.FromResult(Artifact(runId, TrainingArtifactKind.MergedGguf, "staged", TrainingArtifactSmokeState.Pending)
                         with
                         {
                             Sha256 = callInfo.ArgAt<string>(2),
                             SizeBytes = callInfo.ArgAt<long>(3)
                         }));
            _ = store.SetArtifactSmokeStateAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<TrainingArtifactSmokeState>(), Arg.Any<string?>(),
                         Arg.Any<CancellationToken>())
                     .Returns(callInfo =>
                     {
                         harnessBox[0].RecordedSmokeState = callInfo.ArgAt<TrainingArtifactSmokeState>(2);
                         harnessBox[0].RecordedSmokeReason = callInfo.ArgAt<string?>(3);
                         return Task.FromResult(Artifact(runId, TrainingArtifactKind.MergedGguf, "staged", callInfo.ArgAt<TrainingArtifactSmokeState>(2)));
                     });
            _ = store.GetArtifactAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(callInfo => Task.FromResult<TrainingArtifactRecord?>(Artifact(runId, TrainingArtifactKind.MergedGguf, "staged",
                         TrainingArtifactSmokeState.Pending) with
                     {
                         Id = callInfo.ArgAt<Guid>(0)
                     }));
        }

        private static TrainingArtifactRecord Artifact(Guid runId, TrainingArtifactKind kind, string path, TrainingArtifactSmokeState state) =>
            new(Guid.NewGuid(), runId, kind, path, Sha256: null, SizeBytes: 0, state, SmokeReason: null, CommittedModelName: null,
                Version: 1, CreatedAtUtc: 0, UpdatedAtUtc: 0);
    }
}
