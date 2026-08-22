namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <inheritdoc />
internal sealed class TransientLlamaServerLauncher(
    ILlamaCppBinaryManager binaryManager,
    IGpuVariantSelector variantSelector,
    ILlamaServerProcessLauncher launcher,
    ILlamaServerHealthProbe healthProbe,
    ILogger<TransientLlamaServerLauncher> logger) : ITransientLlamaServerLauncher
{
    /// <summary>How often the readiness race re-checks whether the child died instead of becoming ready.</summary>
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ILlamaCppBinaryManager _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
    private readonly ILlamaServerHealthProbe _healthProbe = healthProbe ?? throw new ArgumentNullException(nameof(healthProbe));
    private readonly ILlamaServerProcessLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    private readonly ILogger<TransientLlamaServerLauncher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IGpuVariantSelector _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));

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
            await WaitForReadyOrExitAsync(handle, spec.BaseAddress, request.ReadinessTimeout, ct).ConfigureAwait(false);
            return await body(new TransientLlamaServerSession(spec.BaseAddress, modelId), ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                handle.TreeKill();
            }
            catch (Exception exception)
            {
                // Best-effort: disposal below still releases the OS resources, and a teardown failure must not mask
                // the outcome the caller came for.
                _logger.LogWarning(exception, "The transient llama-server (pid {ProcessId}) could not be tree-killed.", handle.ProcessId);
            }

            handle.Dispose();
        }
    }

    internal async Task<TransientLlamaServerEvaluationResult<T>> RunEvaluationAsync<T>(TransientLlamaServerEvaluationRequest request,
        LlamaBinary binary,
        GpuVariant variant,
        LlamaServerCapabilityManifest manifest,
        ILlamaServerLaunchPolicy launchPolicy,
        Func<TransientLlamaServerEvaluationProvenance, CancellationToken, Task> bindProvenance,
        Func<TransientLlamaServerEvaluationSession, CancellationToken, Task<T>> body,
        CancellationToken ct)
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

        var model = await CaptureModelProvenanceAsync(request.ModelFilePath, request.AdapterFilePath, ct).ConfigureAwait(false);
        var resolved = ResolvedLaunchArguments.Replay(request.ContextTokens);
        LlamaServerLaunchPlan? plan = variant == GpuVariant.Cpu ? launchPolicy.ResolveCpuReplayPlan(resolved) : null;
        var modelId = Path.GetFileName(request.ModelFilePath);
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
            Arguments = [.. spec.Arguments, "--alias", endpointModelAlias]
        };
        var capabilityDecision = LlamaServerCapabilityGate.Apply(spec, manifest, requireMetrics: false);
        if (!capabilityDecision.IsCompatible)
        {
            throw new LlamaRuntimeException(capabilityDecision.SanitizedError ?? "The frozen evaluation launch is not supported by this runtime.");
        }

        spec = capabilityDecision.Spec;
        var handle = _launcher.Launch(spec);
        var processId = handle.ProcessId;
        var treeKillRequested = false;
        var processExitObserved = false;
        T value;
        LlamaServerLaunchReceipt receipt;
        try
        {
            await WaitForReadyOrExitAsync(handle,
                spec.BaseAddress,
                request.ReadinessTimeout,
                ct,
                endpointModelAlias).ConfigureAwait(false);
            var stableModel = await CaptureModelProvenanceAsync(request.ModelFilePath, request.AdapterFilePath, ct).ConfigureAwait(false);
            if (stableModel != model)
            {
                throw new LlamaRuntimeException("The evaluation model changed while the runtime was loading it.");
            }

            int? effectiveContext = null;
            try
            {
                effectiveContext = await _healthProbe.TryReadEffectiveContextTokensAsync(spec.BaseAddress, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogDebug(exception, "The transient evaluation runtime did not report an effective context window.");
            }

            receipt = LlamaServerProcessSupervisor.BuildBenchmarkLaunchReceipt(variant,
                manifest.Version ?? binary.Version,
                manifest.ExecutableSha256,
                LlamaServerLaunchProjection.TryFromArguments(spec.Arguments)
                ?? LlamaServerLaunchProjection.From(variant,
                    resolved,
                    plan,
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
                capabilityDecision.OmittedOptions);
            var session = new TransientLlamaServerEvaluationSession(spec.BaseAddress, endpointModelAlias, model, receipt);
            await bindProvenance(session.Provenance, ct).ConfigureAwait(false);
            value = await body(session, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                treeKillRequested = true;
                handle.TreeKill();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "The transient evaluation llama-server (pid {ProcessId}) could not be tree-killed.", processId);
            }

            try
            {
                processExitObserved = await handle.WaitForExitAsync(request.TeardownTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                handle.Dispose();
            }
        }

        return new TransientLlamaServerEvaluationResult<T>(value,
            model,
            receipt,
            new TransientLlamaServerTeardownEvidence(processId,
                treeKillRequested,
                processExitObserved,
                ExitObservationTimedOut: !processExitObserved,
                HandleDisposed: true));
    }

    private static async Task<TransientLlamaServerModelProvenance> CaptureModelProvenanceAsync(string modelPath,
        string? adapterPath,
        CancellationToken ct)
    {
        var model = await CaptureFileAsync(modelPath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(adapterPath))
        {
            return new TransientLlamaServerModelProvenance(Path.GetFileName(modelPath),
                model.SizeBytes,
                model.Sha256,
                AdapterId: null,
                AdapterSizeBytes: null,
                AdapterSha256: null);
        }

        var adapter = await CaptureFileAsync(adapterPath, ct).ConfigureAwait(false);
        return new TransientLlamaServerModelProvenance(Path.GetFileName(modelPath),
            model.SizeBytes,
            model.Sha256,
            Path.GetFileName(adapterPath),
            adapter.SizeBytes,
            adapter.Sha256);
    }

    private static async Task<FileIdentity> CaptureFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        return new FileIdentity(stream.Length, digest);
    }

    private readonly record struct FileIdentity(long SizeBytes, string Sha256);

    /// <summary>
    ///     A port the OS just told us is free. There is a window between the probe and llama-server's own bind, which
    ///     is the same window the supervisor's allocator lives with. Evaluation launches close that ownership gap by
    ///     verifying a unique <c>--alias</c> through <c>/v1/models</c> before exposing the endpoint; the legacy export
    ///     smoke path retains its original readiness-only behavior.
    /// </summary>
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
    private async Task WaitForReadyOrExitAsync(ILlamaServerProcessHandle handle,
        Uri baseAddress,
        TimeSpan readinessTimeout,
        CancellationToken ct,
        string? expectedModelAlias = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var readyTask = expectedModelAlias is null
            ? _healthProbe.WaitForReadyAsync(baseAddress, readinessTimeout, linked.Token)
            : _healthProbe.WaitForReadyAndVerifyModelAliasAsync(baseAddress,
                expectedModelAlias,
                readinessTimeout,
                linked.Token);
        var exitTask = WatchForExitAsync(handle, linked.Token);
        var winner = await Task.WhenAny(readyTask, exitTask).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            if (winner == exitTask && handle.HasExited)
            {
                throw new LlamaRuntimeException("The model runtime exited while loading the model.");
            }

            if (!await readyTask.ConfigureAwait(false))
            {
                throw new LlamaRuntimeException("The model runtime did not become ready in time.");
            }
        }
        finally
        {
            await SwallowCancellationAsync(readyTask).ConfigureAwait(false);
            await SwallowCancellationAsync(exitTask).ConfigureAwait(false);
        }
    }

    private static async Task WatchForExitAsync(ILlamaServerProcessHandle handle, CancellationToken ct)
    {
        while (!handle.HasExited)
        {
            await Task.Delay(ExitPollInterval, ct).ConfigureAwait(false);
        }
    }

    private static async Task SwallowCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Both arms are abandoned once the race is decided; only the winner's outcome is reported.
        }
    }
}
