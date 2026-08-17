namespace XE_Local_AI_Engine.Client.Services.Training.Export;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     Turns a finished run's staged HF adapter into a servable GGUF, then proves it loads.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Exclusivity.</strong> An export takes the same hold a run does — an exclusive
///         <see cref="IGpuWorkGate" /> admission and the llama.cpp runtime-mutation lease — acquired
///         BEFORE anything is written, so a refusal is an immediate, harmless 409. The merge step genuinely needs the
///         GPU; the adapter conversion does not, but it is held to the same single-flight rule rather than given its
///         own concurrency model for a path that runs for seconds.
///     </para>
///     <para>
///         <strong>The run's status is deliberately never moved.</strong> Training already terminalized the run and
///         its work item, and the store's terminal transitions are one-way by construction: moving a
///         <c>Succeeded</c> run to <c>Exporting</c> is refused, and even if it were not, nothing could move it back
///         (<c>CompleteRunAsync</c> is a no-op once the work item is terminal). An export failure must not be able to
///         flip a finished run to failed, so the export's own outcome lives entirely on the artifact row — digest,
///         smoke state, and reason — with live progress on the run hub. A run that succeeded stays succeeded.
///     </para>
/// </remarks>
public sealed class TrainingExportService(
    IServiceScopeFactory scopeFactory,
    ITrainingRunEventBuffer events,
    IGpuWorkGate gpuWorkGate,
    ILlamaServerProcessSupervisor supervisor,
    ITrainingRuntimeService runtime,
    ITrainingProcessSpawner spawner,
    IConvertScriptProvisioner convertScripts,
    ILlamaCppBinaryManager binaryManager,
    IGpuVariantSelector variantSelector,
    IGgufImportInspector inspector,
    ITrainedModelSmokeGate smokeGate,
    TrainingRunWorkspace workspace,
    INodeDataDirectory dataDirectory,
    ILogger<TrainingExportService> logger) : ITrainingExportService
{
    private const string MergedCheckpointDirectoryName = "merged-hf";

    /// <summary>How many trailing subprocess lines reach the run's log tail. Enough to carry a stack, not a whole build log.</summary>
    private const int SubprocessTailLines = 40;

    /// <summary>
    ///     Absolute ceiling on one export. A merge writes the whole model twice and a quantize reads it again, so the
    ///     bound is generous — it exists to stop a wedged subprocess holding the GPU forever, not to pace the work.
    /// </summary>
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromHours(3);

    private readonly ILlamaCppBinaryManager _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
    private readonly IConvertScriptProvisioner _convertScripts = convertScripts ?? throw new ArgumentNullException(nameof(convertScripts));
    private readonly INodeDataDirectory _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
    private readonly ITrainingRunEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly IGpuWorkGate _gpuWorkGate = gpuWorkGate ?? throw new ArgumentNullException(nameof(gpuWorkGate));
    private readonly IGgufImportInspector _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    private readonly ILogger<TrainingExportService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ITrainingRuntimeService _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly ITrainedModelSmokeGate _smokeGate = smokeGate ?? throw new ArgumentNullException(nameof(smokeGate));
    private readonly ITrainingProcessSpawner _spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
    private readonly ILlamaServerProcessSupervisor _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
    private readonly IGpuVariantSelector _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));
    private readonly TrainingRunWorkspace _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

    /// <summary>Awaited by tests so a started export can be observed to completion; null when nothing is running.</summary>
    internal Task? InFlight { get; private set; }

    public async Task<TrainingExportStart> StartExportAsync(Guid runId,
        TrainingExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == TrainingArtifactKind.HfAdapterDir)
        {
            return new TrainingExportStart(TrainingExportStartOutcome.RunNotExportable,
                "The trainer's own adapter directory is not an export target.");
        }

        var quantization = request.Kind == TrainingArtifactKind.MergedGguf
            ? TrainingExportQuantizations.TryNormalize(request.QuantType)
            : TrainingExportQuantizations.Float16;
        if (quantization is null)
        {
            return new TrainingExportStart(TrainingExportStartOutcome.UnsupportedQuantization,
                "The requested quantization is not supported by this export.");
        }

        if (_runtime.ResolveInterpreterPath() is not { } interpreter || _runtime.GetStatus().Phase != TrainingRuntimePhase.Ready)
        {
            return new TrainingExportStart(TrainingExportStartOutcome.RuntimeUnavailable,
                "The Python training runtime is not installed.");
        }

        var (plan, planRefusal) = await BuildPlanAsync(runId, request.Kind, quantization, cancellationToken).ConfigureAwait(false);
        if (planRefusal is { } refusal)
        {
            return refusal;
        }

        // Acquired in the queue's order and BEFORE anything is written, so a busy box costs a refused request rather
        // than a half-written staged file.
        var activity = _gpuWorkGate.TryBeginExclusive(GpuWorkKind.Export);
        if (activity is null)
        {
            return new TrainingExportStart(TrainingExportStartOutcome.Busy, "Training or another export is already running.");
        }

        ILlamaServerRuntimeMutationLease? lease = null;
        try
        {
            lease = await _supervisor.TryAcquireRuntimeMutationLeaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            activity.Dispose();
            throw;
        }

        if (lease is null)
        {
            activity.Dispose();
            return new TrainingExportStart(TrainingExportStartOutcome.Busy,
                "A model is loaded. Eject it and try the export again.");
        }

        // Detached on purpose: the endpoint answers 202 and the operator follows the export on the run hub. The
        // request's own token is NOT flowed in — it dies with the HTTP response, which would kill the export.
        InFlight = Task.Run(() => RunPipelineAsync(plan!, interpreter, activity, lease), CancellationToken.None);
        return new TrainingExportStart(TrainingExportStartOutcome.Accepted);
    }

    public async Task<TrainedModelSmokeResult> RunSmokeAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITrainingRunStore>();
        var artifact = await store.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false)
                       ?? throw new TrainingExportRejectedException("The artifact was not found.");
        if (artifact.CommittedModelName is not null)
        {
            throw new TrainingExportRejectedException("The artifact is already promoted; re-testing it would not change the registry.");
        }

        var view = await ResolveSmokeViewAsync(store, artifact, cancellationToken).ConfigureAwait(false);
        var activity = _gpuWorkGate.TryBeginExclusive(GpuWorkKind.Export)
                       ?? throw new TrainingExportRejectedException("Training or an export is already running.");
        ILlamaServerRuntimeMutationLease? lease = null;
        try
        {
            lease = await _supervisor.TryAcquireRuntimeMutationLeaseAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new TrainingExportRejectedException("A model is loaded. Eject it and try the smoke test again.");
            var result = await _smokeGate.RunAsync(view, cancellationToken).ConfigureAwait(false);
            _ = await store.SetArtifactSmokeStateAsync(artifact.Id, artifact.Version, result.State, result.Reason, cancellationToken)
                           .ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            activity.Dispose();
        }
    }

    public async Task DeleteArtifactAsync(Guid artifactId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITrainingRunStore>();
        // Read before the delete purely to learn the staged path; the STORE still decides the outcome, and an unknown
        // id, a stale version or a promoted artifact all raise from the call below — before anything on disk moves.
        var artifact = await store.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
        await store.DeleteArtifactAsync(artifactId, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (artifact is not null)
        {
            DeleteStagedBytes(artifact);
        }
    }

    /// <summary>
    ///     Validates the run and decides every path the pipeline will use, before any exclusivity is taken. A refusal
    ///     here has nothing to clean up.
    /// </summary>
    private async Task<PlanOrRefusal> BuildPlanAsync(Guid runId,
        TrainingArtifactKind kind,
        string quantization,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITrainingRunStore>();
        var run = await store.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null || run.Status != TrainingRunStatus.Succeeded)
        {
            return new PlanOrRefusal(Value: null, new TrainingExportStart(TrainingExportStartOutcome.RunNotExportable,
                "Only a run that finished successfully can be exported."));
        }

        var artifacts = await store.ListArtifactsAsync(runId, cancellationToken).ConfigureAwait(false);
        if (artifacts.FirstOrDefault(item => item.Kind == TrainingArtifactKind.HfAdapterDir) is not { } adapter
            || !Directory.Exists(adapter.Path))
        {
            return new PlanOrRefusal(Value: null, new TrainingExportStart(TrainingExportStartOutcome.RunNotExportable,
                "The run has no staged adapter to export."));
        }

        if (artifacts.Any(item => item.Kind == kind && item.CommittedModelName is not null))
        {
            return new PlanOrRefusal(Value: null, new TrainingExportStart(TrainingExportStartOutcome.RunNotExportable,
                "This export was already promoted. Delete the registered model first."));
        }

        var staged = _workspace.StagedDirectory(runId);
        var fileName = kind == TrainingArtifactKind.MergedGguf
            ? TrainingExportPaths.MergedGgufName(quantization)
            : TrainingExportPaths.AdapterGgufName();
        return new PlanOrRefusal(new ExportPlan(runId,
            kind,
            quantization,
            adapter.Path,
            BaseArtifactManifest.ResolveDirectory(_dataDirectory, run.BaseArtifactId),
            staged,
            Path.Combine(staged, fileName),
            run.LinkedInstalledModelName),
            Refusal: null);
    }

    private async Task RunPipelineAsync(ExportPlan plan,
        string interpreter,
        IDisposable activity,
        ILlamaServerRuntimeMutationLease lease)
    {
        using var cancellation = new CancellationTokenSource(ExportTimeout);
        var cancellationToken = cancellation.Token;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITrainingRunStore>();

        // Replacing a previous unpromoted attempt happens HERE rather than during planning: the staged file name is
        // deterministic, so a second export overwrites the bytes and the old row's digest would silently stop
        // describing them — but a start that got refused for a busy GPU must not have destroyed the old row on its
        // way out.
        await DeleteStaleArtifactsAsync(store, plan).ConfigureAwait(false);

        // Created up front so EVERY outcome — including a merge that never produces a file — is durably visible on
        // the run rather than surviving only as a hub event the operator may not have been watching for.
        var artifact = await store.CreateArtifactAsync(new TrainingArtifactInput(plan.RunId, plan.Kind, plan.OutputPath), CancellationToken.None)
                                  .ConfigureAwait(false);
        Publish(plan.RunId, "preparing", null);
        try
        {
            TrainingRunWorkspace.CreateOwnerOnlyDirectory(_workspace.WorkDirectory(plan.RunId));
            var scripts = await _convertScripts.EnsureAsync(cancellationToken).ConfigureAwait(false);
            await ProduceGgufAsync(plan, interpreter, scripts, cancellationToken).ConfigureAwait(false);

            var (sha256, sizeBytes) = await ComputeDigestAsync(plan.OutputPath, cancellationToken).ConfigureAwait(false);
            artifact = await store.SetArtifactDigestAsync(artifact.Id, artifact.Version, sha256, sizeBytes, cancellationToken)
                                  .ConfigureAwait(false);

            // Preview BEFORE smoke: an architecture llama.cpp has no chat support for would fail the smoke test with
            // a load error that says nothing useful, and the operator would be left guessing why.
            Publish(plan.RunId, "inspecting", null);
            if (await RejectUnsupportedShapeAsync(plan, cancellationToken).ConfigureAwait(false) is { } rejection)
            {
                _ = await store.SetArtifactSmokeStateAsync(artifact.Id, artifact.Version, TrainingArtifactSmokeState.Skipped, rejection,
                                   CancellationToken.None)
                               .ConfigureAwait(false);
                Publish(plan.RunId, "skipped", rejection);
                return;
            }

            Publish(plan.RunId, "smoke", null);
            var view = new TrainingArtifactRecordView(plan.OutputPath, await ResolveBaseModelPathAsync(scope, plan, cancellationToken).ConfigureAwait(false));
            var result = await _smokeGate.RunAsync(view, cancellationToken).ConfigureAwait(false);
            _ = await store.SetArtifactSmokeStateAsync(artifact.Id, artifact.Version, result.State, result.Reason, CancellationToken.None)
                           .ConfigureAwait(false);
            Publish(plan.RunId, result.State == TrainingArtifactSmokeState.Passed ? "ready" : "smokeFailed", result.Reason);
        }
        catch (Exception exception)
        {
            var reason = Describe(exception);
            _logger.LogError(exception, "The export for training run {RunId} failed.", plan.RunId);
            try
            {
                var current = await store.GetArtifactAsync(artifact.Id, CancellationToken.None).ConfigureAwait(false);
                if (current is not null)
                {
                    _ = await store.SetArtifactSmokeStateAsync(current.Id, current.Version, TrainingArtifactSmokeState.Failed, reason,
                                       CancellationToken.None)
                                   .ConfigureAwait(false);
                }
            }
            catch (Exception recordFailure)
            {
                _logger.LogError(recordFailure, "The export failure for training run {RunId} could not be recorded.", plan.RunId);
            }

            Publish(plan.RunId, "failed", reason);
        }
        finally
        {
            // Both intermediates are multi-gigabyte and neither is recoverable value once the run is over. They are
            // swept on EVERY path, failures included: a quantize that died leaves an f16 file no artifact row points
            // at, which is a silent disk leak nothing would ever clean up.
            DeleteBestEffort(Path.Combine(plan.StagedDirectory, MergedCheckpointDirectoryName), directory: true);
            DeleteIntermediateFloatFile(plan);
            _workspace.DeleteWorkDirectory(plan.RunId);
            await lease.DisposeAsync().ConfigureAwait(false);
            activity.Dispose();
        }
    }

    private async Task DeleteStaleArtifactsAsync(ITrainingRunStore store, ExportPlan plan)
    {
        var artifacts = await store.ListArtifactsAsync(plan.RunId, CancellationToken.None).ConfigureAwait(false);
        foreach (var stale in artifacts.Where(item => item.Kind == plan.Kind && item.CommittedModelName is null))
        {
            await store.DeleteArtifactAsync(stale.Id, stale.Version, CancellationToken.None).ConfigureAwait(false);
            DeleteStagedBytes(stale);
        }
    }

    /// <summary>
    ///     Removes the bytes an artifact row pointed at, once that row is already gone. Contained by construction: only
    ///     a path inside the run's OWN staged directory is touched, and never the directory itself — an artifact row is
    ///     operator-facing state, and a path that somehow escaped must cost a log line rather than a recursive delete
    ///     somewhere else on the box. A failure is logged for the same reason: the row is gone either way, so leaked
    ///     bytes must at least be visible.
    /// </summary>
    private void DeleteStagedBytes(TrainingArtifactRecord artifact)
    {
        var staged = Path.GetFullPath(_workspace.StagedDirectory(artifact.RunId));
        var path = Path.GetFullPath(artifact.Path);
        if (!path.StartsWith(staged.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            _logger.LogWarning("Artifact {ArtifactId} of run {RunId} was deleted, but its path is not inside the run's staged directory, so the bytes were left in place.",
                artifact.Id, artifact.RunId);
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The staged bytes of artifact {ArtifactId} could not be deleted; the row is gone and the files are leaked.",
                artifact.Id);
        }
    }

    private async Task ProduceGgufAsync(ExportPlan plan, string interpreter, ConvertScriptPaths scripts, CancellationToken cancellationToken)
    {
        if (plan.Kind == TrainingArtifactKind.AdapterGguf)
        {
            Publish(plan.RunId, "converting", null);
            await RunSubprocessAsync(plan,
                    interpreter,
                    [
                        scripts.LoraToGgufScriptPath,
                        "--base", plan.BaseCheckpointDirectory,
                        "--outtype", "f16",
                        "--outfile", plan.OutputPath,
                        plan.AdapterDirectory
                    ],
                    scripts.GgufPyDirectory,
                    "adapter conversion",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        Publish(plan.RunId, "merging", null);
        var mergedDirectory = Path.Combine(plan.StagedDirectory, MergedCheckpointDirectoryName);
        var jobPath = await WriteExportJobAsync(plan, cancellationToken).ConfigureAwait(false);
        var exportScript = Path.Combine(TrainingScripts.ResolveDirectory(), TrainingScripts.ExportScriptName);
        if (!File.Exists(exportScript))
        {
            throw new TrainingExportRejectedException("The exporter script is missing from this installation.");
        }

        // No gguf-py on the merge step's PYTHONPATH: it runs unsloth, not a llama.cpp script, and widening the
        // subprocess's import path beyond what it needs is how an unrelated shadowing bug gets introduced later.
        await RunSubprocessAsync(plan, interpreter, [exportScript, "--config", jobPath], ggufPyDirectory: null, "merge", cancellationToken)
            .ConfigureAwait(false);
        if (!Directory.Exists(mergedDirectory))
        {
            throw new TrainingExportRejectedException("The merge finished without writing a merged checkpoint.");
        }

        Publish(plan.RunId, "converting", null);
        var floatPath = Path.Combine(plan.StagedDirectory, TrainingExportPaths.MergedGgufName(TrainingExportQuantizations.Float16));
        await RunSubprocessAsync(plan,
                interpreter,
                [scripts.HfToGgufScriptPath, "--outtype", "f16", "--outfile", floatPath, mergedDirectory],
                scripts.GgufPyDirectory,
                "GGUF conversion",
                cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(plan.Quantization, TrainingExportQuantizations.Float16, StringComparison.Ordinal))
        {
            // The f16 conversion IS the requested output; nothing to quantize and nothing to clean up.
            return;
        }

        Publish(plan.RunId, "quantizing", null);
        await QuantizeAsync(plan, floatPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes the f16 conversion the quantizer read from — unless it IS the requested output, in which case it
    ///     is the artifact and deleting it would throw the export away.
    /// </summary>
    private static void DeleteIntermediateFloatFile(ExportPlan plan)
    {
        if (plan.Kind != TrainingArtifactKind.MergedGguf
            || string.Equals(plan.Quantization, TrainingExportQuantizations.Float16, StringComparison.Ordinal))
        {
            return;
        }

        DeleteBestEffort(Path.Combine(plan.StagedDirectory, TrainingExportPaths.MergedGgufName(TrainingExportQuantizations.Float16)),
            directory: false);
    }

    private async Task QuantizeAsync(ExportPlan plan, string floatPath, CancellationToken cancellationToken)
    {
        var variant = await _variantSelector.SelectVariantAsync(cancellationToken).ConfigureAwait(false);
        var binary = await _binaryManager.EnsureBinaryAsync(variant, cancellationToken).ConfigureAwait(false);
        if (binary.QuantizerExecutablePath is not { } quantizer)
        {
            // Named precisely because the fix is specific: every upstream prebuilt archive omits llama-quantize, so
            // the operator has to build the runtime from source to quantize at all.
            throw new TrainingExportRejectedException(
                "This llama.cpp runtime does not include llama-quantize, so a merged model cannot be quantized. Build the runtime from source, or export at F16.");
        }

        await RunSubprocessAsync(plan, quantizer, [floatPath, plan.OutputPath, plan.Quantization], ggufPyDirectory: null, "quantization",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Runs one export subprocess to completion, folding the trainer stdio protocol's <c>error</c> line into the
    ///     failure message when the child speaks it and falling back to the exit status when it does not (the
    ///     conversion scripts and the quantizer print plain text).
    /// </summary>
    private async Task RunSubprocessAsync(ExportPlan plan,
        string executable,
        IReadOnlyList<string> arguments,
        string? ggufPyDirectory,
        string step,
        CancellationToken cancellationToken)
    {
        using var handle = _spawner.Spawn(new TrainingSpawnRequest(executable,
            arguments,
            _workspace.WorkDirectory(plan.RunId),
            Guid.NewGuid().ToString("N"),
            ggufPyDirectory));
        using var registration = cancellationToken.Register(handle.KillGroup);

        string? protocolError = null;
        var tail = new Queue<string>();
        await foreach (var line in handle.ReadOutputAsync(cancellationToken).ConfigureAwait(false))
        {
            if (TrainingRunStdioParser.TryParse(line) is { Kind: TrainingStdioEventKind.Error } parsed)
            {
                protocolError = parsed.Message ?? parsed.Category;
            }

            tail.Enqueue(line);
            while (tail.Count > SubprocessTailLines)
            {
                _ = tail.Dequeue();
            }
        }

        var exitCode = await handle.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await AppendLogAsync(plan.RunId, step, tail).ConfigureAwait(false);
        if (exitCode == 0 && protocolError is null)
        {
            return;
        }

        throw new TrainingExportRejectedException(protocolError is { Length: > 0 }
            ? $"The {step} step failed: {protocolError}"
            : $"The {step} step exited with status {exitCode}.");
    }

    /// <summary>
    ///     Inspects the produced GGUF the way a promotion would, so an artifact that could never be committed is
    ///     rejected here — visibly, on the artifact — instead of at the end of a promotion the operator expected to work.
    /// </summary>
    private async Task<string?> RejectUnsupportedShapeAsync(ExportPlan plan, CancellationToken cancellationToken)
    {
        var inspection = await _inspector.InspectAsync(new GgufImportSource(plan.OutputPath),
                                             GgufImportInspectionMode.InProcessTrainedCommit,
                                             cancellationToken)
                                         .ConfigureAwait(false);
        var expected = plan.Kind == TrainingArtifactKind.AdapterGguf ? GgufImportWorkload.LoraAdapter : GgufImportWorkload.CausalChat;
        if (inspection.Workload == expected)
        {
            return null;
        }

        var architecture = inspection.Architecture ?? "unknown";
        return inspection.Rejections.Contains(GgufImportRejectionCode.UnsupportedArchitecture)
            ? $"architecture {architecture} is not in this engine's supported set, so the export cannot be registered"
            : $"the exported file is not a loadable {expected} GGUF (architecture {architecture})";
    }

    private static async Task<string?> ResolveBaseModelPathAsync(AsyncServiceScope scope, ExportPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Kind != TrainingArtifactKind.AdapterGguf)
        {
            return null;
        }

        if (plan.LinkedInstalledModelName is not { Length: > 0 } baseModel)
        {
            throw new TrainingExportRejectedException(
                "This run is not linked to an installed model, so its adapter has no base model to be tested against.");
        }

        var models = scope.ServiceProvider.GetRequiredService<IGgufModelStore>();
        return await models.ResolveModelFilePathAsync(baseModel, cancellationToken).ConfigureAwait(false)
               ?? throw new TrainingExportRejectedException("The installed base model this adapter applies to is no longer available.");
    }

    private async Task<TrainingArtifactRecordView> ResolveSmokeViewAsync(ITrainingRunStore store,
        TrainingArtifactRecord artifact,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(artifact.Path))
        {
            throw new TrainingExportRejectedException("The staged artifact file is no longer on disk.");
        }

        if (artifact.Kind != TrainingArtifactKind.AdapterGguf)
        {
            return new TrainingArtifactRecordView(artifact.Path, BaseModelFilePath: null);
        }

        var run = await store.GetAsync(artifact.RunId, cancellationToken).ConfigureAwait(false);
        if (run?.LinkedInstalledModelName is not { Length: > 0 } baseModel)
        {
            throw new TrainingExportRejectedException(
                "This run is not linked to an installed model, so its adapter has no base model to be tested against.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var models = scope.ServiceProvider.GetRequiredService<IGgufModelStore>();
        var basePath = await models.ResolveModelFilePathAsync(baseModel, cancellationToken).ConfigureAwait(false)
                       ?? throw new TrainingExportRejectedException("The installed base model this adapter applies to is no longer available.");
        return new TrainingArtifactRecordView(artifact.Path, basePath);
    }

    private async Task<string> WriteExportJobAsync(ExportPlan plan, CancellationToken cancellationToken)
    {
        var job = new TrainingExportJobConfigV1
        {
            ContractVersion = TrainingRunStdioParser.ContractVersion,
            BasePath = plan.BaseCheckpointDirectory,
            AdapterDir = plan.AdapterDirectory,
            OutputDir = plan.StagedDirectory
        };
        var path = Path.Combine(_workspace.WorkDirectory(plan.RunId), "export-job.json");
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(job, TrainingJson.Options), cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static async Task<FileDigest> ComputeDigestAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new TrainingExportRejectedException("The export finished without writing its output file.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new FileDigest(Convert.ToHexStringLower(hash), stream.Length);
    }

    private async Task AppendLogAsync(Guid runId, string step, IEnumerable<string> tail)
    {
        var builder = new StringBuilder().Append("export: ").AppendLine(step);
        foreach (var line in tail)
        {
            _ = builder.AppendLine(line);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITrainingRunStore>();
        await store.AppendLogTailAsync(runId, builder.ToString(), CancellationToken.None).ConfigureAwait(false);
    }

    private void Publish(Guid runId, string phase, string? message) =>
        _ = _events.Append(runId, TrainingRunEventKind.Export, new TrainingRunPayload(Phase: phase, Message: message));

    private static string Describe(Exception exception) =>
        exception switch
        {
            TrainingExportRejectedException => exception.Message,
            LlamaRuntimeException => exception.Message,
            OperationCanceledException => "The export was cancelled or exceeded its time limit.",
            _ => "The export failed."
        };

    private static void DeleteBestEffort(string path, bool directory)
    {
        try
        {
            if (directory && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (!directory)
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leftover bytes cost disk, not correctness; the operator can delete the artifact to clear the run.
        }
    }

    /// <summary>Everything the pipeline needs, resolved once before any exclusivity is taken.</summary>
    private sealed record ExportPlan(
        Guid RunId,
        TrainingArtifactKind Kind,
        string Quantization,
        string AdapterDirectory,
        string BaseCheckpointDirectory,
        string StagedDirectory,
        string OutputPath,
        string? LinkedInstalledModelName);

    // The outcome of planning an export: exactly one side is set — a plan the pipeline can run, or the refusal to
    // return to the caller. A refusal happens before any exclusivity is taken, so it has nothing to clean up.
    private sealed record PlanOrRefusal(ExportPlan? Value, TrainingExportStart? Refusal);

    // A produced export file's content digest and size on disk.
    private sealed record FileDigest(string Sha256, long SizeBytes);
}
