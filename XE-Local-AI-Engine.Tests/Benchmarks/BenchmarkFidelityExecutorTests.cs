namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The fidelity measurement fails CLOSED and replays the run's placement while pinning the window. Both are the
///     whole point of a display-only axis: a number that is not comparable is worse than no number, and a null
///     perplexity beside a real one reads as "this quant lost nothing".
/// </summary>
public sealed class BenchmarkFidelityExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void BuildArguments_PinsTheWindowAt512AndReplaysEverythingElse()
    {
        var runtime = new BenchmarkLlamaRuntimeSnapshotV1(GpuVariant.Cuda,
            ContextTokens: 32_768,
            GpuLayers: 99,
            TensorSplit: "0.6,0.4",
            OverrideTensor: "blk\\.[0-9]+\\.ffn.*=CPU",
            KvTypeK: "q8_0",
            KvTypeV: "q8_0",
            FlashAttention: true,
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1);

        var arguments = BenchmarkFidelityExecutor.BuildArguments("/models/quant.gguf", "/corpus.txt", chunks: 200, runtime, kldBasePath: null, isBasePhase: false);

        // The window is 512 and NOT the run's 32 768: perplexity means nothing across two different windows, and every
        // published llama.cpp number uses this one.
        AssertEx.Equal("512", ValueAfter(arguments, "-c"));
        AssertEx.False(arguments.Contains("32768", StringComparer.Ordinal), "The run's own context must never reach the perplexity argv.");
        AssertEx.Equal("200", ValueAfter(arguments, "--chunks"));

        // Everything that DIFFERS between the runs being compared is replayed, because that is what is being compared.
        AssertEx.Equal("99", ValueAfter(arguments, "--n-gpu-layers"));
        AssertEx.Equal("0.6,0.4", ValueAfter(arguments, "--tensor-split"));
        AssertEx.Equal("blk\\.[0-9]+\\.ffn.*=CPU", ValueAfter(arguments, "--override-tensor"));
        AssertEx.Equal("q8_0", ValueAfter(arguments, "--cache-type-k"));
        AssertEx.Equal("q8_0", ValueAfter(arguments, "--cache-type-v"));
        AssertEx.Equal("on", ValueAfter(arguments, "--flash-attn"));
        AssertEx.False(arguments.Contains("--kl-divergence", StringComparer.Ordinal), "A perplexity-only pass must not ask for divergence.");
    }

    [Test]
    public void BuildArguments_ForTheTwoKldPhases_DiffersOnlyByTheReadFlag()
    {
        var runtime = new BenchmarkLlamaRuntimeSnapshotV1(GpuVariant.Cuda, 4096, null, null, null, null, null, FlashAttention: false,
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1);

        var basePhase = BenchmarkFidelityExecutor.BuildArguments("/base.gguf", "/corpus.txt", 50, runtime, "/cache/abc.logits", isBasePhase: true);
        var quantPhase = BenchmarkFidelityExecutor.BuildArguments("/quant.gguf", "/corpus.txt", 50, runtime, "/cache/abc.logits", isBasePhase: false);

        // The base phase WRITES the file and must not also ask to read it — that would score the base against itself.
        AssertEx.False(basePhase.Contains("--kl-divergence", StringComparer.Ordinal));
        AssertEx.Equal("/cache/abc.logits", ValueAfter(basePhase, "--kl-divergence-base"));
        AssertEx.True(quantPhase.Contains("--kl-divergence", StringComparer.Ordinal));
        AssertEx.Equal("/cache/abc.logits", ValueAfter(quantPhase, "--kl-divergence-base"));
        AssertEx.Equal("off", ValueAfter(basePhase, "--flash-attn"), "Flash attention is replayed as it was frozen, in both phases.");
    }

    [Test]
    public async Task Execute_WhenTheRuntimeHasNoPerplexityTool_FailsWithTheRefusalThatNamesTheFix()
    {
        var harness = new Harness(_root);
        harness.RuntimeHasPerplexityTool = false;

        await harness.Executor().ExecuteAsync(harness.Work(), CancellationToken.None);

        _ = await harness.Store.Received(1)
                         .MarkFidelityFailedAsync(harness.RunId,
                             Harness.WorkVersion,
                             BenchmarkFidelityExecutor.PerplexityUnavailableMessage,
                             Arg.Any<CancellationToken>());
        _ = await harness.Store.DidNotReceiveWithAnyArgs().MarkFidelitySucceededAsync(default!, default);
    }

    [Test]
    public async Task Execute_WhenTheOutputCarriesNoFinalEstimate_TerminalizesFailedWithItsTail()
    {
        var harness = new Harness(_root);
        harness.Output = "llama_model_load: error loading model: unable to allocate CUDA0 buffer";

        await harness.Executor().ExecuteAsync(harness.Work(), CancellationToken.None);

        // Fail closed, and quote what the tool said — a null-scored success would publish "no measurable loss".
        _ = await harness.Store.Received(1)
                         .MarkFidelityFailedAsync(harness.RunId,
                             Harness.WorkVersion,
                             Arg.Is<string>(message => message.Contains("unable to allocate CUDA0 buffer", StringComparison.Ordinal)),
                             Arg.Any<CancellationToken>());
        _ = await harness.Store.DidNotReceiveWithAnyArgs().MarkFidelitySucceededAsync(default!, default);
    }

    [Test]
    public async Task Execute_OnARealFinalEstimate_RecordsTheNumbersTheCorpusAndTheWindow()
    {
        var harness = new Harness(_root);
        BenchmarkFidelitySuccessCommand? command = null;
        _ = harness.Store.MarkFidelitySucceededAsync(Arg.Do<BenchmarkFidelitySuccessCommand>(value => command = value), Arg.Any<CancellationToken>());

        await harness.Executor().ExecuteAsync(harness.Work(), CancellationToken.None);

        var recorded = AssertEx.NotNull(command);
        AssertEx.Equal<double?>(6.7983, recorded.PerplexityMean);
        AssertEx.Equal<double?>(0.07405, recorded.PerplexityStdErr);
        AssertEx.Equal<int?>(BenchmarkFidelityPolicy.ContextTokens, recorded.PerplexityContextTokens);
        AssertEx.Equal(BenchmarkFidelityCorpus.Require().CorpusId, recorded.CorpusId);
        AssertEx.Null(recorded.KldMean, "A perplexity-only pass measured no divergence, so it reports none.");
        AssertEx.Null(recorded.BaseLogitsDigest, "And it therefore carries no comparability digest to gate on.");

        // The reduced evidence block, labelled as reduced: llama-perplexity has no readiness probe, so there is no
        // launch receipt, and storing this under the receipt's shape would let a UI present it as complete evidence.
        var receipt = Encoding.UTF8.GetString(recorded.ReceiptJson.Span);
        AssertEx.True(receipt.Contains("\"kind\":\"fidelity-evidence\"", StringComparison.Ordinal), $"The evidence must say what it is; got {receipt}.");
        AssertEx.True(receipt.Contains("\"argv\"", StringComparison.Ordinal), "The argv is the auditable part of a measurement with no receipt.");
    }

    [Test]
    public async Task Execute_MeasuringKldFromScratch_ReservesForTheBaseModelFirstAndTheQuantSecond()
    {
        // The reservation used to be sized ONCE, off the quant, and then held across a base-logit pass that loads a
        // model routinely larger than it — an over-admission that OOMs on exactly the box where the base is the big
        // one. The two phases never overlap, so each reserves for the model it actually loads.
        var harness = new Harness(_root, kind: "kld");
        harness.ScriptedOutputs.Enqueue(BasePhaseOutput);
        harness.ScriptedOutputs.Enqueue(KlDivergenceOutput);
        BenchmarkFidelitySuccessCommand? command = null;
        _ = harness.Store.MarkFidelitySucceededAsync(Arg.Do<BenchmarkFidelitySuccessCommand>(value => command = value), Arg.Any<CancellationToken>());

        await harness.Executor().ExecuteAsync(harness.Work(), CancellationToken.None);

        AssertEx.Equal(expected: 2, harness.CapacityRequests.Count, "One reservation per phase, taken and released in turn.");
        AssertEx.Equal(Harness.BaseModelName, harness.CapacityRequests[0].ModelName, "The base-logit phase is admitted against the BASE model's footprint.");
        AssertEx.Equal("quant.gguf", harness.CapacityRequests[1].ModelName, "And the quant pass against the quant's, after the base has been released.");
        AssertEx.True(harness.CapacityRequests.All(static request => !request.PublishLaunchAdmission),
            "Neither phase launches a server, so neither publishes a launch admission.");
        AssertEx.Equal<double?>(0.030165, AssertEx.NotNull(command).KldMean, "And the measurement itself still lands.");
    }

    [Test]
    public async Task Execute_WhenTheBaseLogitsAreAlreadyCached_ReservesOnlyForTheQuant()
    {
        // The early return on a published base file loads nothing, so it must reserve nothing either.
        var harness = new Harness(_root, kind: "kld");
        harness.ScriptedOutputs.Enqueue(BasePhaseOutput);
        harness.ScriptedOutputs.Enqueue(KlDivergenceOutput);
        await harness.Executor().ExecuteAsync(harness.Work(), CancellationToken.None);

        var second = new Harness(_root, kind: "kld");
        second.ScriptedOutputs.Enqueue(KlDivergenceOutput);
        await second.Executor().ExecuteAsync(second.Work(), CancellationToken.None);

        AssertEx.Equal(expected: 1, second.CapacityRequests.Count);
        AssertEx.Equal("quant.gguf", second.CapacityRequests[0].ModelName);
    }

    [Test]
    public async Task Execute_WhenAnotherProcessHoldsTheBaseLogitLease_RequeuesInsteadOfFailing()
    {
        // The message has always promised a retry; failing the item was terminal, and a fidelity work item pins
        // attempt = 1, so there was nothing behind that promise. It goes back to Queued instead.
        var harness = new Harness(_root, kind: "kld");
        var key = BenchmarkKldCacheKey.Create(Harness.BaseFingerprint, BenchmarkFidelityCorpus.Require().Sha256, BenchmarkFidelityPolicy.DefaultChunks);
        using var heldByAnotherProcess = AssertEx.NotNull(harness.Cache.TryAcquireLease(key));

        await harness.Executor().ExecuteAsync(harness.Work(), CancellationToken.None);

        _ = await harness.Store.Received(1)
                         .RequeueFidelityAsync(harness.RunId, Harness.WorkVersion, Arg.Any<string>(), Arg.Any<CancellationToken>());
        _ = await harness.Store.DidNotReceive()
                         .MarkFidelityFailedAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _ = await harness.Store.DidNotReceiveWithAnyArgs().MarkFidelitySucceededAsync(default!, default);
    }

    /// <summary>
    ///     The watchdog's linked token produced an OperationCanceledException that the executor recorded as an
    ///     operator cancellation with no reason — a two-hour runaway was indistinguishable from someone pressing
    ///     stop. Classification is derived at mapping time: the caller's token is not cancelled, so it is ours.
    /// </summary>
    [Test]
    public async Task Execute_WhenTheMeasurementWatchdogFires_FailsWithATimeoutReasonRatherThanCancelling()
    {
        var harness = new Harness(_root);

        await harness.Executor(new HangingPerplexity(), TimeSpan.FromMilliseconds(50)).ExecuteAsync(harness.Work(), CancellationToken.None);

        _ = await harness.Store.Received(1)
                         .MarkFidelityFailedAsync(harness.RunId,
                             Harness.WorkVersion,
                             BenchmarkFidelityExecutor.MeasurementTimedOutMessage,
                             Arg.Any<CancellationToken>());
        _ = await harness.Store.DidNotReceive().MarkFidelityCancelledAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    private const string BasePhaseOutput = """
                                           0.31.519.491 I perplexity: 7.18 seconds per pass - ETA 5.97 minutes
                                           1.12.579.214 I Final estimate: PPL = 5.7712 +/- 0.38886
                                           """;

    /// <summary>A real <c>--kl-divergence</c> tail: no `Final estimate` line, `±` separators, `Same top p`.</summary>
    private const string KlDivergenceOutput = """
                                              ====== Perplexity statistics ======
                                              Mean PPL(Q)                   :   5.886524 ±   0.398426
                                              Mean PPL(base)                :   5.771204 ±   0.388860

                                              ====== KL divergence statistics ======
                                              Mean    KLD:   0.030165 ±   0.002043
                                              99.0%   KLD:   0.388019

                                              ====== Token probability statistics ======
                                              Same top p: 91.529 ± 0.780 %
                                              """;

    private static string? ValueAfter(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        return index < 0 || index + 1 >= arguments.Count ? null : arguments[index + 1];
    }

    private sealed class Harness
    {
        public const long WorkVersion = 7;

        private static readonly string Fingerprint = "v1:" + new string('a', 64);
        private readonly BenchmarkKldBaseCache _cache;
        private readonly BenchmarkRunRecord _run;
        private readonly string _withTool;
        private readonly string _withoutTool;
        private readonly BenchmarkRuntimeSnapshotV1 _snapshot;

        public Harness(string root, string kind = "ppl")
        {
            var installed = InstalledModel();
            var baseModel = BaseModel();
            _snapshot = SnapshotFor(installed);
            _run = RunFor(_snapshot);
            RunId = _run.Id;
            _cache = new BenchmarkKldBaseCache(new StubFreeSpace(long.MaxValue), Path.Combine(root, "kld"));
            _withTool = Path.Combine(root, "runtime-with-tool");
            _withoutTool = Path.Combine(root, "runtime-without-tool");
            Directory.CreateDirectory(_withTool);
            Directory.CreateDirectory(_withoutTool);
            File.WriteAllText(Path.Combine(_withTool, "llama-server"), "server");
            File.WriteAllText(Path.Combine(_withTool, LlamaCppToolBinaries.PerplexityFileName), "perplexity");
            File.WriteAllText(Path.Combine(_withoutTool, "llama-server"), "server");

            Store = Substitute.For<IBenchmarkStore>();
            _ = Store.GetFidelityAttemptAsync(AttemptId, Arg.Any<CancellationToken>())
                     .Returns(new BenchmarkFidelityAttemptRecord(AttemptId, RunId, 1, kind, BenchmarkJudgeAttemptStatus.Running,
                         null, null, null, null, null, null, null, null, null, null, null, null, 1, null, null));
            _ = Store.GetProjectAsync(_run.ProjectId, Arg.Any<CancellationToken>())
                     .Returns(new BenchmarkProjectRecord(_run.ProjectId, "p", new byte[]
                         {
                             1
                         }, 4096, Guid.NewGuid(), false, null, true, 1, 1, 1,
                         FidelityEnabled: true,
                         FidelityKldEnabled: kind == "kld",
                         FidelityKldBaseModelName: BaseModelName,
                         FidelityKldBaseFingerprint: BaseFingerprint));
            _ = Store.ListLiveFidelityDigestsAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlySet<string>>(_ => new HashSet<string>(StringComparer.Ordinal));
            _leases = new Dictionary<string, IBenchmarkInstalledModelLease>(StringComparer.Ordinal)
            {
                [installed.ModelName] = new StubLease(installed),
                [BaseModelName] = new StubLease(baseModel)
            };
            Gguf = Substitute.For<IGgufModelStore>();
            _ = Gguf.ResolveModelFilePathAsync(installed.ModelName, Arg.Any<CancellationToken>()).Returns("/models/quant.gguf");
            _ = Gguf.ResolveModelFilePathAsync(BaseModelName, Arg.Any<CancellationToken>()).Returns("/models/base.gguf");
        }

        public const string BaseModelName = "base.gguf";
        private readonly Dictionary<string, IBenchmarkInstalledModelLease> _leases;

        public static string BaseFingerprint { get; } = "v1:" + new string('e', 64);
        public static Guid AttemptId { get; } = new("44444444-4444-4444-4444-444444444444");
        public IBenchmarkStore Store { get; }
        public Guid RunId { get; }
        public IGgufModelStore Gguf { get; }

        /// <summary>The base-logit cache the executor writes to, so a test can hold its lease the way a rival process would.</summary>
        public BenchmarkKldBaseCache Cache => _cache;

        /// <summary>Whether the resolved runtime directory contains the perplexity helper at all.</summary>
        public bool RuntimeHasPerplexityTool { get; set; } = true;

        /// <summary>Every capacity request the executor made, in the order it made them.</summary>
        public List<CapacityRequest> CapacityRequests { get; } = [];

        /// <summary>Consumed in order — the KLD path runs the base phase first and the quant pass second.</summary>
        public Queue<string> ScriptedOutputs { get; } = new();

        public string Output { get; set; } = """
                                             0.31.519.491 I perplexity: 7.18 seconds per pass - ETA 5.97 minutes
                                             1.12.579.214 I Final estimate: PPL = 6.7983 +/- 0.07405
                                             """;

        public BenchmarkClaimedWork Work() =>
            new(3, RunId, BenchmarkWorkKind.Fidelity, 1, WorkVersion, _run, null, AttemptId);

        /// <summary>
        ///     PerplexityExecutablePath resolves off DISK, beside the server binary, so the two cases are two real
        ///     directories rather than a flag — that resolution is the seam the "this runtime cannot measure
        ///     fidelity" refusal actually reads.
        /// </summary>
        private static ILlamaCppBinaryManager Binaries(string serverPath)
        {
            var binaries = Substitute.For<ILlamaCppBinaryManager>();
            _ = binaries.EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                        .Returns(call => new LlamaBinary(serverPath, "b10201", call.Arg<GpuVariant>(), false));
            return binaries;
        }

        private ICapacityService AllowingCapacity()
        {
            var capacity = Substitute.For<ICapacityService>();
            _ = capacity.DecideAsync(Arg.Do<CapacityRequest>(request => CapacityRequests.Add(request)), Arg.Any<CancellationToken>())
                        .Returns(new CapacityDecision(CapacityVerdict.Allow, "allowed", OllamaEvictionWarning: false));
            return capacity;
        }

        public BenchmarkFidelityExecutor Executor(IBenchmarkPerplexityRunner? runner = null, TimeSpan? measurementTimeout = null) =>
            new(Store,
                new FixedSnapshots(_snapshot),
                new NamedLeases(_leases),
                Gguf,
                AllowingCapacity(),
                Binaries(Path.Combine(RuntimeHasPerplexityTool ? _withTool : _withoutTool, "llama-server")),
                runner ?? new ScriptedPerplexity(() => ScriptedOutputs.Count > 0 ? ScriptedOutputs.Dequeue() : Output),
                _cache,
                Options.Create(new BenchmarkKldCacheOptions()),
                new StubEnvironment(),
                new BenchmarkCancellationRegistry(),
                new BenchmarkAdmissionRetry(MaxRetries: 0, TimeSpan.Zero),
                NullLogger<BenchmarkFidelityExecutor>.Instance,
                measurementTimeout);

        private static InstalledModelSnapshot InstalledModel()
        {
            var revision = "v1:" + new string('b', 64);
            return new InstalledModelSnapshot("quant.gguf",
                revision,
                [],
                revision,
                [],
                revision,
                LocalModelOrigin.Imported,
                "llamacpp",
                "map-revision",
                "repo/quant",
                "revision",
                "Q4_K_M",
                GgufRole.Chat,
                Fingerprint);
        }

        /// <summary>The KLD base model: a different name AND a different fingerprint, which is what the executor checks.</summary>
        private static InstalledModelSnapshot BaseModel()
        {
            var revision = "v1:" + new string('d', 64);
            return new InstalledModelSnapshot(BaseModelName,
                revision,
                [],
                revision,
                [],
                revision,
                LocalModelOrigin.Imported,
                "llamacpp",
                "map-revision",
                "repo/base",
                "revision",
                "BF16",
                GgufRole.Chat,
                BaseFingerprint);
        }

        private static BenchmarkRuntimeSnapshotV1 SnapshotFor(InstalledModelSnapshot installed) =>
            new(1,
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                "task",
                4096,
                null!,
                new BenchmarkLlamaRuntimeSnapshotV1(GpuVariant.Cuda, 4096, 99, null, null, null, null, FlashAttention: false,
                    LlamaServerBenchmarkLaunchPolicy.DeterministicV1),
                null!,
                new BenchmarkInstalledModelSnapshotV1(installed.ModelName,
                    installed.RegistryRevision,
                    [],
                    installed.RegistryAliasSetHash,
                    [],
                    installed.PhysicalMemberSetHash,
                    installed.Origin,
                    installed.ProviderName!,
                    installed.ProviderMappingRevision,
                    installed.RepoId,
                    installed.SourceRevision,
                    installed.ModelName,
                    installed.Quantization,
                    installed.Role.ToString(),
                    installed.ModelContentFingerprint),
                null!,
                "1.0.0",
                1,
                "hash");

        private static BenchmarkRunRecord RunFor(BenchmarkRuntimeSnapshotV1 snapshot) =>
            new(Guid.NewGuid(),
                snapshot.ProjectId,
                new byte[]
                {
                    1
                },
                snapshot.PrimaryModel.ModelName,
                snapshot.PrimaryModel.Origin,
                snapshot.PrimaryModel.ModelContentFingerprint,
                "Agent",
                1,
                4096,
                BenchmarkPrimaryStatus.Succeeded,
                4096,
                10,
                5,
                500,
                null,
                1,
                null,
                null,
                1,
                1,
                1,
                1,
                1);
    }

    private sealed class FixedSnapshots(BenchmarkRuntimeSnapshotV1 snapshot) : IBenchmarkRuntimeSnapshotFactory
    {
        public BenchmarkRuntimeSnapshotV1 Create(BenchmarkRuntimeSnapshotInput input) =>
            throw new NotSupportedException();

        public byte[] Serialize(BenchmarkRuntimeSnapshotV1 snapshot) =>
            throw new NotSupportedException();

        public BenchmarkRuntimeSnapshotV1 Deserialize(ReadOnlySpan<byte> payload) =>
            snapshot;
    }

    /// <summary>
    ///     Keyed by model name, because the KLD path leases TWO models — the quant it measures and the base it
    ///     measures against — and the executor verifies each one's fingerprint against a different expectation.
    /// </summary>
    private sealed class NamedLeases(IReadOnlyDictionary<string, IBenchmarkInstalledModelLease> leases) : IBenchmarkInstalledModelLeaseProvider
    {
        public Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken) =>
            leases.TryGetValue(modelName, out var lease) ? Task.FromResult(lease) : throw new KeyNotFoundException(modelName);
    }

    internal sealed class StubLease(InstalledModelSnapshot snapshot) : IBenchmarkInstalledModelLease
    {
        public InstalledModelSnapshot Snapshot { get; } = snapshot;

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }

    /// <summary>A child process that never returns on its own — only the watchdog ends it.</summary>
    private sealed class HangingPerplexity : IBenchmarkPerplexityRunner
    {
        public async Task<BenchmarkPerplexityProcessResult> RunAsync(string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The hanging runner only ever ends by cancellation.");
        }
    }

    private sealed class ScriptedPerplexity(Func<string> output) : IBenchmarkPerplexityRunner
    {
        public async Task<BenchmarkPerplexityProcessResult> RunAsync(string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            // The base phase's whole product is the logit file, and the cache publishes it with a same-directory move.
            // A scripted runner that wrote nothing would fail at the publish instead of exercising the phase ordering.
            var index = arguments.ToList().IndexOf("--kl-divergence-base");
            if (index >= 0 && !arguments.Contains("--kl-divergence", StringComparer.Ordinal))
            {
                await File.WriteAllTextAsync(arguments[index + 1], "logits", cancellationToken).ConfigureAwait(false);
            }

            return new BenchmarkPerplexityProcessResult(0, output());
        }
    }

    private sealed class StubEnvironment : IRuntimeEnvironmentFactsProvider
    {
        public Task<RuntimeEnvironmentFactsV1> CaptureAsync(GpuVariant variant, CancellationToken ct) =>
            Task.FromResult(new RuntimeEnvironmentFactsV1(1, null, null, null, 42, ["hardware"]));
    }

    private sealed class StubFreeSpace(long freeBytes) : IFreeSpaceProbe
    {
        public long GetAvailableFreeBytes(string path) =>
            freeBytes;
    }
}
