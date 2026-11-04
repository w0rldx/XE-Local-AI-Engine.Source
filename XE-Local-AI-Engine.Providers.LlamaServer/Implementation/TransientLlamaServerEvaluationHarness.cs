namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <inheritdoc />
internal sealed class TransientLlamaServerEvaluationHarness : ITransientLlamaServerEvaluationHarness
{
    private readonly ILlamaCppBinaryManager _binaryManager;
    private readonly ILlamaServerCapabilityManifestProbe _capabilityManifestProbe;
    private readonly TransientLlamaServerLauncher _launcher;
    private readonly ILlamaServerLaunchPolicy _launchPolicy;
    private readonly IGpuModelLoadAdmission _loadAdmission;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly IGpuVariantSelector _variantSelector;
    private readonly IGgufMetadataReader _ggufReader;

    public TransientLlamaServerEvaluationHarness(ILlamaServerProcessSupervisor supervisor,
        ILlamaCppBinaryManager binaryManager,
        IGpuVariantSelector variantSelector,
        ILlamaServerCapabilityManifestProbe capabilityManifestProbe,
        ILlamaServerLaunchPolicy launchPolicy,
        TransientLlamaServerLauncher launcher,
        IGpuModelLoadAdmission loadAdmission,
        IGgufMetadataReader ggufReader)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(binaryManager);
        ArgumentNullException.ThrowIfNull(variantSelector);
        ArgumentNullException.ThrowIfNull(capabilityManifestProbe);
        ArgumentNullException.ThrowIfNull(launchPolicy);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(loadAdmission);
        ArgumentNullException.ThrowIfNull(ggufReader);
        _supervisor = supervisor;
        _binaryManager = binaryManager;
        _variantSelector = variantSelector;
        _capabilityManifestProbe = capabilityManifestProbe;
        _launchPolicy = launchPolicy;
        _launcher = launcher;
        _loadAdmission = loadAdmission;
        _ggufReader = ggufReader;
    }

    public Task<TransientLlamaServerEvaluationResult<T>> RunAsync<T>(TransientLlamaServerEvaluationRequest request,
        Func<TransientLlamaServerEvaluationProvenance, CancellationToken, Task> bindProvenance,
        Func<TransientLlamaServerEvaluationSession, CancellationToken, Task<T>> body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(bindProvenance);
        ArgumentNullException.ThrowIfNull(body);
        EnsureSupportedPolicy(request);

        return WithRuntimeAsync(async (runtime, token) =>
        {
            var controlVector = await TransientLlamaServerControlVectorCheck.CheckOrNullAsync(request, runtime.Manifest, _ggufReader, token).ConfigureAwait(false);
            return await RunOneAsync(request, runtime, bindProvenance, body, pinnedModel: null, controlVector, token).ConfigureAwait(false);
        }, ct);
    }

    public Task<IReadOnlyList<TransientLlamaServerEvaluationResult<T>>> RunArmsAsync<T>(IReadOnlyList<TransientLlamaServerEvaluationRequest> requests,
        Func<int, TransientLlamaServerEvaluationSession, CancellationToken, Task<T>> body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(body);
        if (requests.Count == 0)
        {
            throw new ArgumentException("A run needs at least one arm.", nameof(requests));
        }

        foreach (var request in requests)
        {
            EnsureSupportedPolicy(request);
            if (!string.IsNullOrWhiteSpace(request.AdapterFilePath))
            {
                throw new ArgumentException("An arm does not take an adapter.", nameof(requests));
            }
        }

        return WithRuntimeAsync<IReadOnlyList<TransientLlamaServerEvaluationResult<T>>>(async (runtime, token) =>
        {
            // All refusals come before the first load, so a bad vector in the last arm costs no load at all.
            var controlVectors = new TransientLlamaServerControlVector?[requests.Count];
            for (var arm = 0; arm < requests.Count; arm++)
            {
                controlVectors[arm] = await TransientLlamaServerControlVectorCheck.CheckOrNullAsync(requests[arm], runtime.Manifest, _ggufReader, token).ConfigureAwait(false);
            }

            var pinnedModels = new Dictionary<string, TransientLlamaServerPinnedModel>(StringComparer.Ordinal);
            var results = new List<TransientLlamaServerEvaluationResult<T>>(requests.Count);
            for (var arm = 0; arm < requests.Count; arm++)
            {
                token.ThrowIfCancellationRequested();
                var request = requests[arm];
                if (!pinnedModels.TryGetValue(request.ModelFilePath, out var pinnedModel))
                {
                    pinnedModel = await TransientLlamaServerPinnedModel.CaptureAsync(request.ModelFilePath, token).ConfigureAwait(false);
                    pinnedModels[request.ModelFilePath] = pinnedModel;
                }

                var armIndex = arm;
                var result = await RunOneAsync(request,
                    runtime,
                    static (_, _) => Task.CompletedTask,
                    (session, bodyToken) => body(armIndex, session, bodyToken),
                    pinnedModel,
                    controlVectors[arm],
                    token).ConfigureAwait(false);
                results.Add(result);

                // The next arm would load beside a process that may still hold VRAM.
                if (!result.Teardown.ProcessExitObserved && arm < requests.Count - 1)
                {
                    throw new LlamaRuntimeException(string.Create(CultureInfo.InvariantCulture,
                        $"Arm {arm}'s model runtime (process {result.Teardown.ProcessId}) did not exit, so the next arm was not started and the finished arms' results are lost."));
                }
            }

            return results;
        }, ct);
    }

    private static void EnsureSupportedPolicy(TransientLlamaServerEvaluationRequest request)
    {
        if (!request.LaunchPolicy.IsSupported)
        {
            throw new ArgumentException("The frozen evaluation launch policy is unsupported.", nameof(request));
        }
    }

    private async Task<TResult> WithRuntimeAsync<TResult>(Func<EvaluationRuntime, CancellationToken, Task<TResult>> work, CancellationToken ct)
    {
        var mutationLease = await _supervisor.AcquireRuntimeMutationLeaseOrNullAsync(ct).ConfigureAwait(false);
        if (mutationLease is null)
        {
            throw new LlamaRuntimeException("Transient evaluation requires every supervised local model to be unloaded before it starts.");
        }

        await using (mutationLease.ConfigureAwait(false))
        {
            var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
            var binary = await _binaryManager.EnsureBinaryAsync(variant, mutationLease, ct).ConfigureAwait(false);

            // Follow the served build, as the supervisor and the transient launcher do: it decides the spec's GPU arguments AND whether this evaluation takes a
            // VRAM admission ticket. A GPU build spawned under a Cpu selection would otherwise load onto the device without ever entering the gate.
            var manifest = await _capabilityManifestProbe.GetManifestAsync(binary, ct).ConfigureAwait(false);
            if (!manifest.ProbeSucceeded)
            {
                throw new LlamaRuntimeException("The selected llama.cpp runtime could not report its supported server options.");
            }

            return await work(new EvaluationRuntime
            {
                Binary = binary,
                Variant = binary.Variant,
                Manifest = manifest
            }, ct).ConfigureAwait(false);
        }
    }

    private async Task<TransientLlamaServerEvaluationResult<T>> RunOneAsync<T>(TransientLlamaServerEvaluationRequest request,
        EvaluationRuntime runtime,
        Func<TransientLlamaServerEvaluationProvenance, CancellationToken, Task> bindProvenance,
        Func<TransientLlamaServerEvaluationSession, CancellationToken, Task<T>> body,
        TransientLlamaServerPinnedModel? pinnedModel,
        TransientLlamaServerControlVector? controlVector,
        CancellationToken ct)
    {
        using var loadTicket = runtime.Variant == GpuVariant.Cpu
            ? null
            : await _loadAdmission.AcquireAsync(ct).ConfigureAwait(false);
        return await _launcher.RunEvaluationAsync(request,
                                  runtime.Binary,
                                  runtime.Variant,
                                  runtime.Manifest,
                                  _launchPolicy,
                                  bindProvenance,
                                  body,
                                  ct,
                                  pinnedModel,
                                  controlVector)
                              .ConfigureAwait(false);
    }

    private sealed class EvaluationRuntime
    {
        public required LlamaBinary Binary { get; init; }

        public required GpuVariant Variant { get; init; }

        public required LlamaServerCapabilityManifest Manifest { get; init; }
    }
}
