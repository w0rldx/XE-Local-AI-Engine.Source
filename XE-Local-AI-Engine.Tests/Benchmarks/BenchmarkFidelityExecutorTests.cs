namespace XE_Local_AI_Engine.Tests.Benchmarks;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
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
        var receipt = System.Text.Encoding.UTF8.GetString(recorded.ReceiptJson.Span);
        AssertEx.True(receipt.Contains("\"kind\":\"fidelity-evidence\"", StringComparison.Ordinal), $"The evidence must say what it is; got {receipt}.");
        AssertEx.True(receipt.Contains("\"argv\"", StringComparison.Ordinal), "The argv is the auditable part of a measurement with no receipt.");
    }

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

        public Harness(string root)
        {
            var installed = InstalledModel();
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
                     .Returns(new BenchmarkFidelityAttemptRecord(AttemptId, RunId, 1, "ppl", BenchmarkJudgeAttemptStatus.Running,
                         null, null, null, null, null, null, null, null, null, null, null, null, 1, null, null));
            _ = Store.GetProjectAsync(_run.ProjectId, Arg.Any<CancellationToken>())
                     .Returns(new BenchmarkProjectRecord(_run.ProjectId, "p", new byte[] { 1 }, 4096, Guid.NewGuid(), false, null, true, 1, 1, 1,
                         FidelityEnabled: true));
            Lease = new StubLease(installed);
            Gguf = Substitute.For<IGgufModelStore>();
            _ = Gguf.ResolveModelFilePathAsync(installed.ModelName, Arg.Any<CancellationToken>()).Returns("/models/quant.gguf");
        }

        public static Guid AttemptId { get; } = new("44444444-4444-4444-4444-444444444444");
        public IBenchmarkStore Store { get; }
        public Guid RunId { get; }
        public StubLease Lease { get; }
        public IGgufModelStore Gguf { get; }
        /// <summary>Whether the resolved runtime directory contains the perplexity helper at all.</summary>
        public bool RuntimeHasPerplexityTool { get; set; } = true;

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

        private static ICapacityService AllowingCapacity()
        {
            var capacity = Substitute.For<ICapacityService>();
            _ = capacity.DecideAsync(Arg.Any<CapacityRequest>(), Arg.Any<CancellationToken>())
                        .Returns(new CapacityDecision(CapacityVerdict.Allow, "allowed", OllamaEvictionWarning: false));
            return capacity;
        }

        public BenchmarkFidelityExecutor Executor() =>
            new(Store,
                new FixedSnapshots(_snapshot),
                new FixedLeases(Lease),
                Gguf,
                AllowingCapacity(),
                Binaries(Path.Combine(RuntimeHasPerplexityTool ? _withTool : _withoutTool, "llama-server")),
                new ScriptedPerplexity(() => Output),
                _cache,
                Options.Create(new BenchmarkKldCacheOptions()),
                new StubEnvironment(),
                new BenchmarkCancellationRegistry(),
                new BenchmarkAdmissionRetry(MaxRetries: 0, TimeSpan.Zero),
                NullLogger<BenchmarkFidelityExecutor>.Instance);

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
                new byte[] { 1 },
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
        public BenchmarkRuntimeSnapshotV1 Create(BenchmarkRuntimeSnapshotInput input) => throw new NotSupportedException();
        public byte[] Serialize(BenchmarkRuntimeSnapshotV1 snapshot) => throw new NotSupportedException();
        public BenchmarkRuntimeSnapshotV1 Deserialize(ReadOnlySpan<byte> payload) => snapshot;
    }

    private sealed class FixedLeases(StubLease lease) : IBenchmarkInstalledModelLeaseProvider
    {
        public Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken) =>
            Task.FromResult<IBenchmarkInstalledModelLease>(lease);
    }

    internal sealed class StubLease(InstalledModelSnapshot snapshot) : IBenchmarkInstalledModelLease
    {
        public InstalledModelSnapshot Snapshot { get; } = snapshot;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedPerplexity(Func<string> output) : IBenchmarkPerplexityRunner
    {
        public Task<BenchmarkPerplexityProcessResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            Task.FromResult(new BenchmarkPerplexityProcessResult(0, output()));
    }

    private sealed class StubEnvironment : IRuntimeEnvironmentFactsProvider
    {
        public Task<RuntimeEnvironmentFactsV1> CaptureAsync(GpuVariant variant, CancellationToken ct) =>
            Task.FromResult(new RuntimeEnvironmentFactsV1(1, null, null, null, 42, ["hardware"]));
    }

    private sealed class StubFreeSpace(long freeBytes) : IFreeSpaceProbe
    {
        public long GetAvailableFreeBytes(string path) => freeBytes;
    }
}
