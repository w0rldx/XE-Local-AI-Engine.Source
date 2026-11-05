namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <inheritdoc />
internal sealed class TransientLlamaServerLauncher : ITransientLlamaServerLauncher
{
    private readonly ILlamaCppBinaryManager _binaryManager;
    private readonly ILlamaServerHealthProbe _healthProbe;
    private readonly ILlamaServerProcessLauncher _launcher;
    private readonly ILogger<TransientLlamaServerLauncher> _logger;

    // The supervisor's own llama-server store, so the one startup reaper also covers a benchmark or smoke child a hard host kill orphaned.
    private readonly ProcessSpawnReceiptStore _spawnReceipts;
    private readonly TimeProvider _timeProvider;
    private readonly IGpuVariantSelector _variantSelector;

    public TransientLlamaServerLauncher(ILlamaCppBinaryManager binaryManager,
        IGpuVariantSelector variantSelector,
        ILlamaServerProcessLauncher launcher,
        ILlamaServerHealthProbe healthProbe,
        ILogger<TransientLlamaServerLauncher> logger,
        TimeProvider timeProvider,
        ProcessSpawnReceiptStore? spawnReceipts = null)
    {
        ArgumentNullException.ThrowIfNull(binaryManager);
        ArgumentNullException.ThrowIfNull(variantSelector);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(healthProbe);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _binaryManager = binaryManager;
        _variantSelector = variantSelector;
        _launcher = launcher;
        _healthProbe = healthProbe;
        _logger = logger;
        _timeProvider = timeProvider;
        _spawnReceipts = spawnReceipts ?? new ProcessSpawnReceiptStore(nodeDataRoot: null, "llama-server", logger);
    }

    public async Task<T> RunAsync<T>(TransientLlamaServerRequest request,
        Func<TransientLlamaServerSession, CancellationToken, Task<T>> body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);
        if (!File.Exists(request.ModelFilePath))
        {
            throw new LlamaRuntimeException("The model file to load was not found.");
        }

        var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
        var binary = await _binaryManager.EnsureBinaryAsync(variant, ct).ConfigureAwait(false);

        // The serve can hand back a build of a different variant than was asked for (a recorded source build outranks a request made against an unseeded signal),
        // and the spec below gates every GPU argument on the variant. Follow the binary being launched, as the supervisor does.
        variant = binary.Variant;
        var modelId = Path.GetFileName(request.ModelFilePath);

        // The key's model name is a LABEL here, not a registry lookup: BuildLaunchSpec only ever puts it on the spec
        // for diagnostics, and every file this spawn touches is an explicit path.
        var key = new LlamaServerProcessSupervisor.ProcessKey(modelId, ModelRole.Chat);
        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(key,
            binary.ServerExecutablePath,
            request.ModelFilePath,
            AllocatePort(),
            variant,
            // Replay, not Explore: a smoke load must not run llama.cpp's auto-fit search, which is a placement
            // decision this throwaway process has no business making on behalf of the next real spawn.
            ResolvedLaunchArguments.Replay(request.ContextTokens),
            chatCacheReuse: 0,
            adapterFilePath: request.AdapterFilePath);

        var handle = _launcher.Launch(spec);
        try
        {
            // Inside the try, so a cancelled receipt write still tears the raw handle down; the finally's dispose removes the receipt.
            handle = await _spawnReceipts.TrackAsync(handle, spec.ExecutablePath, $"{modelId} transient port {spec.Port}", ct).ConfigureAwait(false);
            await WaitForReadyOrExitAsync(handle, spec.BaseAddress, request.ReadinessTimeout, ct).ConfigureAwait(false);
            return await body(new TransientLlamaServerSession
            {
                BaseAddress = spec.BaseAddress,
                ModelId = modelId
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            await TearDownAsync(handle, handle.ProcessId, exitWait: null).ConfigureAwait(false);
        }
    }

    internal async Task<TransientLlamaServerEvaluationResult<T>> RunEvaluationAsync<T>(TransientLlamaServerEvaluationRequest request,
        LlamaBinary binary,
        GpuVariant variant,
        LlamaServerCapabilityManifest manifest,
        ILlamaServerLaunchPolicy launchPolicy,
        Func<TransientLlamaServerEvaluationProvenance, CancellationToken, Task> bindProvenance,
        Func<TransientLlamaServerEvaluationSession, CancellationToken, Task<T>> body,
        CancellationToken ct,
        TransientLlamaServerPinnedModel? pinnedModel = null,
        TransientLlamaServerControlVector? controlVector = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(launchPolicy);
        ArgumentNullException.ThrowIfNull(bindProvenance);
        ArgumentNullException.ThrowIfNull(body);
        if (request.TeardownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The teardown timeout must be positive.");
        }

        if (!File.Exists(request.ModelFilePath)
            || !string.IsNullOrWhiteSpace(request.AdapterFilePath) && !File.Exists(request.AdapterFilePath))
        {
            throw new LlamaRuntimeException("The evaluation model file to load was not found.");
        }

        var model = pinnedModel?.Provenance
                    ?? await CaptureModelProvenanceAsync(request.ModelFilePath, request.AdapterFilePath, ct).ConfigureAwait(false);
        var modelId = Path.GetFileName(request.ModelFilePath);
        var launch = ComposeEvaluationLaunch(request, binary, variant, manifest, launchPolicy, controlVector, modelId);
        var spec = launch.Spec;
        var handle = _launcher.Launch(spec);
        var processId = handle.ProcessId;
        bool processExitObserved;
        T value;
        LlamaServerLaunchReceipt receipt;
        try
        {
            handle = await _spawnReceipts.TrackAsync(handle, spec.ExecutablePath, $"{modelId} evaluation port {spec.Port}", ct).ConfigureAwait(false);
            await WaitForReadyOrExitAsync(handle,
                spec.BaseAddress,
                request.ReadinessTimeout,
                ct,
                launch.Alias).ConfigureAwait(false);
            await EnsureInputsUnchangedAsync(request, model, pinnedModel, controlVector, ct).ConfigureAwait(false);
            var effectiveContext = await ReadEffectiveContextOrNullAsync(spec.BaseAddress, ct).ConfigureAwait(false);
            receipt = await BuildEvaluationReceiptAsync(request, binary, variant, manifest, launch, effectiveContext, processId).ConfigureAwait(false);
            var session = new TransientLlamaServerEvaluationSession
            {
                BaseAddress = spec.BaseAddress,
                ModelId = launch.Alias,
                Model = model,
                Launch = receipt,
                ControlVector = controlVector
            };
            await bindProvenance(session.Provenance, ct).ConfigureAwait(false);
            value = await body(session, ct).ConfigureAwait(false);
        }
        finally
        {
            processExitObserved = await TearDownAsync(handle, processId, request.TeardownTimeout).ConfigureAwait(false);
        }

        return new TransientLlamaServerEvaluationResult<T>
        {
            Value = value,
            Model = model,
            Launch = receipt,
            Teardown = new TransientLlamaServerTeardownEvidence
            {
                ProcessId = processId,
                TreeKillRequested = true,
                ProcessExitObserved = processExitObserved,
                ExitObservationTimedOut = !processExitObserved,
                HandleDisposed = true
            },
            ControlVector = controlVector
        };
    }

    internal static async Task<TransientLlamaServerModelProvenance> CaptureModelProvenanceAsync(string modelPath,
        string? adapterPath,
        CancellationToken ct)
    {
        var model = await FileSha256.ComputeWithLengthAsync(modelPath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(adapterPath))
        {
            return new TransientLlamaServerModelProvenance
            {
                ModelId = Path.GetFileName(modelPath),
                ModelSizeBytes = model.SizeBytes,
                ModelSha256 = model.Sha256,
                AdapterId = null,
                AdapterSizeBytes = null,
                AdapterSha256 = null
            };
        }

        var adapter = await FileSha256.ComputeWithLengthAsync(adapterPath, ct).ConfigureAwait(false);
        return new TransientLlamaServerModelProvenance
        {
            ModelId = Path.GetFileName(modelPath),
            ModelSizeBytes = model.SizeBytes,
            ModelSha256 = model.Sha256,
            AdapterId = Path.GetFileName(adapterPath),
            AdapterSizeBytes = adapter.SizeBytes,
            AdapterSha256 = adapter.Sha256
        };
    }

    /// <summary>
    ///     Composes the frozen evaluation launch, throwing before anything starts when the runtime cannot honor it.
    /// </summary>
    private static EvaluationLaunch ComposeEvaluationLaunch(TransientLlamaServerEvaluationRequest request,
        LlamaBinary binary,
        GpuVariant variant,
        LlamaServerCapabilityManifest manifest,
        ILlamaServerLaunchPolicy launchPolicy,
        TransientLlamaServerControlVector? controlVector,
        string modelId)
    {
        var resolved = ResolvedLaunchArguments.Replay(request.ContextTokens);
        LlamaServerLaunchPlan? plan = variant == GpuVariant.Cpu ? launchPolicy.ResolveCpuReplayPlan(resolved) : null;
        if (!manifest.SupportsOption("--alias"))
        {
            throw new LlamaRuntimeException("The selected llama.cpp runtime cannot prove transient endpoint ownership.");
        }

        var endpointModelAlias = $"xe-evaluation-{Guid.NewGuid():N}";
        var key = new LlamaServerProcessSupervisor.ProcessKey(modelId, ModelRole.Chat);
        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(key,
            binary.ServerExecutablePath,
            request.ModelFilePath,
            AllocatePort(),
            variant,
            resolved,
            request.LaunchPolicy.ChatCacheReuse,
            SpeculativeDecodingSettings.Disabled,
            plan,
            request.LaunchPolicy.ChatCacheRamMiB,
            adapterFilePath: request.AdapterFilePath);
        spec = spec with
        {
            Arguments = controlVector is null
                ? [.. spec.Arguments, "--alias", endpointModelAlias]
                :
                [
                    .. spec.Arguments, "--alias", endpointModelAlias,
                    LlamaServerManagedFlags.ControlVectorScaled, TransientLlamaServerControlVectorCheck.FormatArgument(request.ControlVectorPath!, controlVector.Scale)
                ]
        };
        var capabilityDecision = LlamaServerCapabilityGate.Apply(spec, manifest, requireMetrics: false);
        if (!capabilityDecision.IsCompatible)
        {
            throw new LlamaRuntimeException(capabilityDecision.SanitizedError ?? "The frozen evaluation launch is not supported by this runtime.");
        }

        return new EvaluationLaunch
        {
            Spec = capabilityDecision.Spec,
            Plan = plan,
            Resolved = resolved,
            Alias = endpointModelAlias,
            OmittedOptions = capabilityDecision.OmittedOptions
        };
    }

    /// <summary>
    ///     Rechecks the evaluation inputs after the runtime loaded them, so a file swapped during the load cannot pass
    ///     as the one the provenance names.
    /// </summary>
    private static async Task EnsureInputsUnchangedAsync(TransientLlamaServerEvaluationRequest request,
        TransientLlamaServerModelProvenance model,
        TransientLlamaServerPinnedModel? pinnedModel,
        TransientLlamaServerControlVector? controlVector,
        CancellationToken ct)
    {
        var modelIsStable = pinnedModel?.IsUnchanged()
                            ?? await CaptureModelProvenanceAsync(request.ModelFilePath, request.AdapterFilePath, ct).ConfigureAwait(false) == model;
        if (!modelIsStable)
        {
            throw new LlamaRuntimeException("The evaluation model changed while the runtime was loading it.");
        }

        if (controlVector is not null
            && !await TransientLlamaServerControlVectorCheck.IsUnchangedAsync(request.ControlVectorPath!, controlVector, ct).ConfigureAwait(false))
        {
            throw new LlamaRuntimeException("The control vector changed while the runtime was loading it.");
        }
    }

    private async Task<int?> ReadEffectiveContextOrNullAsync(Uri baseAddress, CancellationToken ct)
    {
        try
        {
            return await _healthProbe.ReadEffectiveContextTokensOrNullAsync(baseAddress, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "The transient evaluation runtime did not report an effective context window.");
            return null;
        }
    }

    private static Task<LlamaServerLaunchReceipt> BuildEvaluationReceiptAsync(TransientLlamaServerEvaluationRequest request,
        LlamaBinary binary,
        GpuVariant variant,
        LlamaServerCapabilityManifest manifest,
        EvaluationLaunch launch,
        int? effectiveContext,
        int processId)
    {
        return LlamaServerProcessSupervisor.BuildBenchmarkLaunchReceiptAsync(variant,
            manifest.Version ?? binary.Version,
            manifest.ExecutableSha256,
            LlamaServerLaunchProjection.FromArgumentsOrNull(launch.Spec.Arguments)
            ?? LlamaServerLaunchProjection.From(variant,
                launch.Resolved,
                launch.Plan,
                ModelRole.Chat,
                request.LaunchPolicy.ChatCacheReuse,
                request.LaunchPolicy.ChatCacheRamMiB),
            new LlamaServerLaunchAuxAssets(!string.IsNullOrWhiteSpace(request.AdapterFilePath), HasMmproj: false, HasDraft: false),
            new LlamaServerLaunchPlacement(variant == GpuVariant.Cpu ? LlamaServerPlacementOutcome.Cpu : LlamaServerPlacementOutcome.Unknown,
                OffloadedLayers: null,
                TotalLayers: null),
            effectiveContext,
            request.LaunchPolicy,
            processId,
            launch.OmittedOptions);
    }

    /// <summary>
    ///     Kills the process tree, waits for the exit when <paramref name="exitWait" /> is set, then disposes the handle.
    /// </summary>
    /// <returns>Whether the exit was observed; always <see langword="false" /> without a wait.</returns>
    private async Task<bool> TearDownAsync(IProcessTreeHandle handle, int processId, TimeSpan? exitWait)
    {
        try
        {
            handle.TreeKill();
        }
        catch (Exception exception)
        {
            // Best-effort: disposal below still releases the OS resources, and a teardown failure must not mask
            // the outcome the caller came for.
            _logger.LogWarning(exception, "The transient llama-server (pid {ProcessId}) could not be tree-killed.", processId);
        }

        try
        {
            return exitWait is { } wait && await handle.WaitForExitAsync(wait, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>
    ///     A port the OS just reported free.
    /// </summary>
    /// <remarks>
    ///     There is a window between the probe and llama-server's own bind, the same window the supervisor's allocator
    ///     lives with. Evaluation launches close that ownership gap by verifying a unique <c>--alias</c> through
    ///     <c>/v1/models</c> before exposing the endpoint; the export smoke path stays readiness-only.
    /// </remarks>
    private static int AllocatePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, port: 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>
    ///     Races readiness against the child dying. Without the exit arm, a model the runtime cannot load would burn
    ///     the whole readiness budget before reporting a failure the first second already proved.
    /// </summary>
    private async Task WaitForReadyOrExitAsync(IProcessTreeHandle handle,
        Uri baseAddress,
        TimeSpan readinessTimeout,
        CancellationToken ct,
        string? expectedModelAlias = null)
    {
        var outcome = await ProcessReadinessRace.RunAsync(handle,
                                                    token => expectedModelAlias is null
                                                        ? _healthProbe.WaitForReadyAsync(baseAddress, readinessTimeout, token)
                                                        : _healthProbe.WaitForReadyAndVerifyModelAliasAsync(baseAddress, expectedModelAlias, readinessTimeout, token),
                                                    _timeProvider,
                                                    ct)
                                                .ConfigureAwait(false);

        if (outcome == ProcessReadinessOutcome.Exited)
        {
            throw new LlamaRuntimeException("The model runtime exited while loading the model.");
        }

        if (outcome == ProcessReadinessOutcome.TimedOut)
        {
            throw new LlamaRuntimeException("The model runtime did not become ready in time.");
        }
    }

    private sealed record EvaluationLaunch
    {
        public required LlamaServerLaunchSpec Spec { get; init; }

        public required LlamaServerLaunchPlan? Plan { get; init; }

        public required ResolvedLaunchArguments Resolved { get; init; }

        public required string Alias { get; init; }

        public required IReadOnlyList<string> OmittedOptions { get; init; }
    }
}
