namespace XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

#pragma warning disable CA1725, S927 // Compact external-seam fakes keep local names; the production contracts remain unchanged.
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Only the external processes, transient runtime and final registry I/O are scripted.</summary>
public static class TrainingLifecycleE2ETestDoubles
{
    public const string InstalledBaseModel = "e2e-base:Q4_K_M";
    public const string InstalledBaseFingerprint = "e2e-base-fingerprint";

    public enum Stage
    {
        RunFrozen,
        TrainingSucceeded,
        ExportStaged,
        SmokePassed,
        BaseEvaluationSucceeded,
        TunedEvaluationSucceeded,
        ComparisonCreated,
        QualityPassed,
        Promoted
    }

    public sealed class Verdicts
    {
        private readonly object _gate = new();
        private readonly HashSet<Stage> _stages = [];

        public void Reset()
        {
            lock (_gate) { _stages.Clear(); }
        }

        public void Record(Stage stage)
        {
            lock (_gate) { _ = _stages.Add(stage); }
        }

        public IReadOnlyList<Stage> Snapshot()
        {
            lock (_gate) { return _stages.OrderBy(static x => x).ToArray(); }
        }

        public void AssertComplete()
        {
            var missing = Enum.GetValues<Stage>().Except(Snapshot()).ToArray();
            if (missing.Length != 0)
            {
                throw new InvalidOperationException("Training lifecycle E2E skipped required verdicts: " + string.Join(", ", missing));
            }
        }
    }

    public sealed class Defaults : ITrainingOptionDefaultsCalculator
    {
        private static readonly TrainingRunDefaults Value = new(new TrainingRunOptionsV1(),
            new TrainingFootprintEstimate(1, 1, 1, 1, Experimental: false), 1, VramKnown: true, Fits: true, RejectionReason: null);

        public Task<TrainingRunDefaults> ComputeAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Value);

        public Task<TrainingRunDefaults> ResolveAsync(Guid id, TrainingRunOptionsV1? requested, CancellationToken ct = default) =>
            Task.FromResult(requested is null
                ? Value
                : Value with
                {
                    Options = requested
                });

        public Task<TrainingFootprintEstimate> EstimateAsync(Guid id, TrainingRunOptionsV1 options, CancellationToken ct = default) =>
            Task.FromResult(Value.Estimate);
    }

    public sealed class Linker : IInstalledBaseModelLinker
    {
        private static readonly InstalledBaseModelLink Link = new(InstalledBaseModel, "e2e/base", InstalledBaseFingerprint);

        public Task<IReadOnlyList<InstalledBaseModelLink>> SuggestAsync(string repo, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InstalledBaseModelLink>>([Link]);

        public Task<InstalledBaseModelLink?> ResolveAsync(string repo, string? name, CancellationToken ct = default) =>
            string.IsNullOrWhiteSpace(name) || string.Equals(name, InstalledBaseModel, StringComparison.Ordinal)
                ? Task.FromResult<InstalledBaseModelLink?>(Link)
                : throw new TrainingRunRejectedException($"'{name}' is not the deterministic E2E base model.");
    }

    public sealed class Runtime : ITrainingRuntimeService
    {
        public Task<TrainingRuntimeInstallResult> InstallAsync(CancellationToken ct) =>
            Task.FromResult(new TrainingRuntimeInstallResult(TrainingRuntimeInstallOutcome.AlreadyRunning));

        public TrainingRuntimeStatus GetStatus() =>
            new(TrainingRuntimePhase.Ready, false, true, [], 0, null, null, null, null);

        public Task<bool> RemoveAsync(CancellationToken ct) =>
            Task.FromResult(false);

        public bool Cancel() =>
            false;

        public string ResolveInterpreterPath() =>
            "/e2e/python";
    }

    public sealed class Capacity : ITrainingCapacityGate
    {
        public Task<TrainingCapacityReservation> ReserveAsync(TrainingFootprintEstimate estimate, CancellationToken ct = default) =>
            Task.FromResult(new TrainingCapacityReservation(true, null, null));
    }

    public sealed class ProcessSpawner : ITrainingProcessSpawner
    {
        public ITrainingProcessHandle Spawn(TrainingSpawnRequest request)
        {
            IReadOnlyList<string> lines = [];
            if (request.Arguments[0].EndsWith("train.py", StringComparison.Ordinal))
            {
                using var job = JsonDocument.Parse(File.ReadAllBytes(request.Arguments[2]));
                var output = job.RootElement.GetProperty("outputDir").GetString()!;
                var adapter = Path.Combine(output, "adapter");
                Directory.CreateDirectory(adapter);
                File.WriteAllText(Path.Combine(adapter, "adapter_config.json"), "{}");
                lines =
                [
                    "{\"event\":\"handshake\",\"contractVersion\":1}",
                    "{\"event\":\"phase\",\"phase\":\"training\"}",
                    JsonSerializer.Serialize(new
                    {
                        @event = "artifact",
                        path = adapter
                    }),
                    "{\"event\":\"done\",\"cancelled\":false}"
                ];
            }
            else if (request.Arguments[0].EndsWith("export.py", StringComparison.Ordinal))
            {
                using var job = JsonDocument.Parse(File.ReadAllBytes(request.Arguments[2]));
                Directory.CreateDirectory(Path.Combine(job.RootElement.GetProperty("outputDir").GetString()!, "merged-hf"));
            }
            else if (request.Arguments[0].Contains("convert_hf_to_gguf", StringComparison.Ordinal))
            {
                File.WriteAllText(request.Arguments[4], "e2e-f16");
            }
            else if (request.ExecutablePath.EndsWith(LlamaCppToolBinaries.QuantizerFileName, StringComparison.Ordinal))
            {
                File.WriteAllText(request.Arguments[1], "e2e-quantized");
            }
            else
            {
                throw new InvalidOperationException($"Unscripted training lifecycle spawn: {request.ExecutablePath} {string.Join(' ', request.Arguments)}");
            }

            return new Handle(lines);
        }

        private sealed class Handle(IReadOnlyList<string> lines) : ITrainingProcessHandle
        {
            public TrainingLaunchReceipt Receipt { get; } = new(4242, 4242, "/e2e/python", 1, "e2e-token");

            public async IAsyncEnumerable<string> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct)
            {
                foreach (var line in lines)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return line;
                }

                await Task.CompletedTask.ConfigureAwait(false);
            }

            public Task<int> WaitForExitAsync(CancellationToken ct) =>
                Task.FromResult(0);

            public void RequestStop() { }
            public void KillGroup() { }
            public void Dispose() { }
        }
    }

    public sealed class ConvertScripts : IConvertScriptProvisioner
    {
        private static readonly ConvertScriptPaths Paths = new("/e2e/convert_hf_to_gguf.py", "/e2e/convert_lora_to_gguf.py", "/e2e/gguf-py", "e2e");

        public ConvertScriptPaths TryResolve() =>
            Paths;

        public Task<ConvertScriptPaths> EnsureAsync(CancellationToken ct) =>
            Task.FromResult(Paths);
    }

    public sealed class BinaryManager : ILlamaCppBinaryManager
    {
        private readonly string _server;

        public BinaryManager(string root)
        {
            var bin = Path.Combine(root, "training-lifecycle-runtime");
            Directory.CreateDirectory(bin);
            _server = Path.Combine(bin, "llama-server");
            File.WriteAllText(_server, "server");
            File.WriteAllText(Path.Combine(bin, LlamaCppToolBinaries.QuantizerFileName), "quantizer");
        }

        public Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct) =>
            Task.FromResult(new LlamaBinary(_server, "e2e", GpuVariant.Cpu, true));

        public Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, ILlamaServerRuntimeMutationLease lease, CancellationToken ct) =>
            EnsureBinaryAsync(variant, ct);

        public Task<LlamaBinary> InstallTagAsync(string tag, string assetName, string digest, long size, GpuVariant variant, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<InstalledRuntimeState> AdoptCudaSourceBuildAsync(string directory, string tag, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RemoveCudaSourceBuildAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }

    public sealed class VariantSelector : IGpuVariantSelector
    {
        public Task<GpuVariant> SelectVariantAsync(CancellationToken ct) =>
            Task.FromResult(GpuVariant.Cpu);
    }

    public sealed class Inspector : IGgufImportInspector
    {
        public Task<GgufImportInspection> InspectAsync(GgufImportSource source, GgufImportInspectionMode mode, CancellationToken ct) =>
            Task.FromResult(new GgufImportInspection(new FileInfo(source.AbsolutePath).Length, 3, "llama", GgufImportWorkload.CausalChat,
                "Q4_K_M", Path.GetFileName(source.AbsolutePath), [], []));
    }

    public sealed class TransientLauncher : ITransientLlamaServerLauncher
    {
        public Task<T> RunAsync<T>(TransientLlamaServerRequest request,
            Func<TransientLlamaServerSession, CancellationToken, Task<T>> body,
            CancellationToken ct)
        {
            if (!File.Exists(request.ModelFilePath))
            {
                throw new InvalidOperationException("Smoke was invoked without staged bytes.");
            }

            return body(new TransientLlamaServerSession(new Uri("http://127.0.0.1:1/v1"), Path.GetFileName(request.ModelFilePath)), ct);
        }
    }

    public sealed class PropsHttpClientFactory : IHttpClientFactory
    {
#pragma warning disable CA2000 // The returned HttpClient owns and disposes the handler.
        public HttpClient CreateClient(string name) =>
            new(new PropsHandler());
#pragma warning restore CA2000
        private sealed class PropsHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"chat_template\":\"e2e-template\"}", Encoding.UTF8, "application/json")
                });
        }
    }

    public sealed class InstalledModels(string root) : IGgufModelStore, ITrainingEvaluationInstalledModelLeaseProvider
    {
        private readonly string _path = CreateBase(root);

        private static string CreateBase(string root)
        {
            var path = Path.Combine(root, "training-lifecycle-base.gguf");
            File.WriteAllText(path, "e2e-base-bytes");
            return path;
        }

        public async Task<ITrainingEvaluationInstalledModelLease> AcquireAsync(string modelName, CancellationToken ct) =>
            new Lease(_path, await ShaAsync(_path, ct).ConfigureAwait(false));

        public Task<string?> ResolveModelFilePathAsync(string name, CancellationToken ct) =>
            Task.FromResult<string?>(_path);

        public Task<GgufAdapterLaunch?> ResolveAdapterLaunchAsync(string name, CancellationToken ct) =>
            Task.FromResult<GgufAdapterLaunch?>(null);

        public Task<string?> ResolveProjectorFilePathAsync(string name, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([
                new LocalModelDescriptor
                {
                    ModelName = InstalledBaseModel,
                    ProviderName = "llamacpp",
                    IsAvailable = true,
                    SizeBytes = new FileInfo(_path).Length,
                    ModifiedAt = null,
                    MaxContextTokens = 4096,
                    ModelContentFingerprint = InstalledBaseFingerprint
                }
            ]);

        public Task<string> ResolveModelNameAsync(GgufModelRequest request, CancellationToken ct) =>
            Task.FromResult(InstalledBaseModel);

        public Task<GgufModelHandle> EnsureModelAsync(GgufModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteModelAsync(string name, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<bool> ExistsAsync(string name, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<GgufModelFootprintFacts?> ResolveModelFootprintFactsAsync(string name, CancellationToken ct) =>
            Task.FromResult<GgufModelFootprintFacts?>(null);

        private static async Task<string> ShaAsync(string path, CancellationToken ct)
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }

        private sealed class Lease(string path, string sha) : ITrainingEvaluationInstalledModelLease
        {
            public string ModelFilePath => path;
            public string ModelContentFingerprint => InstalledBaseFingerprint;
            public string ModelSha256 => sha;
            public long ModelSizeBytes => new FileInfo(path).Length;

            public ValueTask DisposeAsync() =>
                ValueTask.CompletedTask;
        }
    }

    public sealed class EvaluationHarness : ITransientLlamaServerEvaluationHarness
    {
        public async Task<TransientLlamaServerEvaluationResult<T>> RunAsync<T>(TransientLlamaServerEvaluationRequest request,
            Func<TransientLlamaServerEvaluationProvenance, CancellationToken, Task> bind,
            Func<TransientLlamaServerEvaluationSession, CancellationToken, Task<T>> body, CancellationToken ct)
        {
            var model = await IdentityAsync(request.ModelFilePath, request.AdapterFilePath, ct).ConfigureAwait(false);
            var launch = LaunchReceipt();
            await bind(new TransientLlamaServerEvaluationProvenance(model, launch), ct).ConfigureAwait(false);
            var session = new TransientLlamaServerEvaluationSession(new Uri("http://127.0.0.1:1/v1"), model.ModelId, model, launch);
            var value = await body(session, ct).ConfigureAwait(false);
            return new TransientLlamaServerEvaluationResult<T>(value, model, launch,
                new TransientLlamaServerTeardownEvidence(4242, true, true, false, true));
        }

        private static async Task<TransientLlamaServerModelProvenance> IdentityAsync(string modelPath, string? adapterPath, CancellationToken ct)
        {
            var model = await File.ReadAllBytesAsync(modelPath, ct).ConfigureAwait(false);
            var adapter = adapterPath is null ? null : await File.ReadAllBytesAsync(adapterPath, ct).ConfigureAwait(false);
            return new(Path.GetFileName(modelPath), model.LongLength, Convert.ToHexStringLower(SHA256.HashData(model)),
                adapterPath is null ? null : Path.GetFileName(adapterPath), adapter?.LongLength,
                adapter is null ? null : Convert.ToHexStringLower(SHA256.HashData(adapter)));
        }

        private static LlamaServerLaunchReceipt LaunchReceipt()
        {
            var projection = new LlamaServerLaunchProjection(false, true, 4096, null, null, null, false, null, null,
                LlamaServerLaunchProjection.FlashAttentionAuto, 4, 4, 512, 512, 1, null, 0, true, null);
            var exactBinaryIdentity = new string('e', 64);
            return new LlamaServerLaunchReceipt(LlamaServerLaunchReceipt.CurrentVersion, GpuVariant.Cpu, "linux", "e2e",
                exactBinaryIdentity, exactBinaryIdentity, projection,
                new LlamaServerLaunchAuxAssets(false, false, false),
                new LlamaServerLaunchPlacement(LlamaServerPlacementOutcome.Unknown, null, null), 4096,
                LlamaServerBenchmarkLaunchPolicy.DeterministicV1);
        }
    }

    public sealed class ChatFactory : IInferenceChatClientFactory
    {
        public IChatClient CreateChatClient(Uri baseAddress, string modelId) =>
            new ScriptedChatClient();

        private sealed class ScriptedChatClient : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
                Task.FromResult(options?.ToolMode == ChatToolMode.RequireAny
                    ? new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                    {
                        new FunctionCallContent("call-1", "get_weather", new Dictionary<string, object?>
                        {
                            ["location"] = "Paris, France"
                        })
                    }))
                    : new ChatResponse(new ChatMessage(ChatRole.Assistant, "no tool call")));

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
                ChatOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
            {
                await Task.CompletedTask.ConfigureAwait(false);
                yield break;
            }

            public object? GetService(Type type, object? key = null) =>
                type.IsInstanceOfType(this) && key is null ? this : null;

            public void Dispose() { }
        }
    }

    public sealed class Importer(Verdicts verdicts) : IGgufModelImporter
    {
        public async Task<PreparedGgufImport> PrepareAsync(GgufImportSource source, GgufImportDestination destination,
            IProgress<GgufImportProgress>? progress, CancellationToken ct)
        {
            var bytes = await File.ReadAllBytesAsync(source.AbsolutePath, ct).ConfigureAwait(false);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var entry = new GgufModelRegistryEntry
            {
                ModelName = destination.CanonicalModelName,
                RepoId = destination.CanonicalModelName,
                FileName = destination.RelativeGgufPath,
                Quant = destination.CanonicalQuant,
                LocalPath = source.AbsolutePath,
                SizeBytes = bytes.LongLength,
                Sha256 = sha,
                SourceRevision = "sha256:" + sha,
                DownloadedAtUtc = DateTimeOffset.UnixEpoch,
                Origin = destination.Origin
            };
            var sidecar = new GgufAcquisitionMetadata
            {
                SchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
                RegistryRevision = "e2e",
                ModelName = destination.CanonicalModelName,
                Origin = destination.Origin,
                LocalFileName = destination.RelativeGgufPath,
                Quantization = destination.CanonicalQuant,
                WeightContentSha256 = sha,
                WeightSizeBytes = bytes.LongLength,
                WeightMemberFingerprint = "e2e-member",
                SourceDisplayName = Path.GetFileName(source.AbsolutePath),
                AcquiredAtUtc = DateTimeOffset.UnixEpoch,
                RegistryRepoId = destination.CanonicalModelName,
                RegistrySourceRevision = "sha256:" + sha,
                Role = GgufRole.Chat,
                ModelContentFingerprint = "e2e-promoted"
            };
            return new PreparedGgufImport("e2e", destination, source.AbsolutePath + ".part", source.AbsolutePath + ".json.part",
                entry, sidecar, "e2e-member", "e2e-promoted");
        }

        public Task<GgufImportCommitReceipt> CommitAsync(PreparedGgufImport prepared, CancellationToken ct)
        {
            verdicts.Record(Stage.Promoted);
            return Task.FromResult(new GgufImportCommitReceipt(prepared.RegistryEntry, prepared.RegistryEntry.LocalPath,
                prepared.RegistryEntry.LocalPath + ".xe-model.json", prepared.WeightMemberFingerprint, prepared.ModelContentFingerprint));
        }

        public Task RollbackCommittedAsync(GgufImportCommitReceipt receipt, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DiscardPreparedAsync(PreparedGgufImport prepared, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
#pragma warning restore CA1725, S927
