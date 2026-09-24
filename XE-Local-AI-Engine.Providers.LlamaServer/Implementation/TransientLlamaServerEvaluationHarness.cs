namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
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

    public TransientLlamaServerEvaluationHarness(ILlamaServerProcessSupervisor supervisor,
        ILlamaCppBinaryManager binaryManager,
        IGpuVariantSelector variantSelector,
        ILlamaServerCapabilityManifestProbe capabilityManifestProbe,
        ILlamaServerLaunchPolicy launchPolicy,
        TransientLlamaServerLauncher launcher,
        IGpuModelLoadAdmission loadAdmission)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(binaryManager);
        ArgumentNullException.ThrowIfNull(variantSelector);
        ArgumentNullException.ThrowIfNull(capabilityManifestProbe);
        ArgumentNullException.ThrowIfNull(launchPolicy);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(loadAdmission);
        _supervisor = supervisor;
        _binaryManager = binaryManager;
        _variantSelector = variantSelector;
        _capabilityManifestProbe = capabilityManifestProbe;
        _launchPolicy = launchPolicy;
        _launcher = launcher;
        _loadAdmission = loadAdmission;
    }

    public async Task<TransientLlamaServerEvaluationResult<T>> RunAsync<T>(TransientLlamaServerEvaluationRequest request,
        Func<TransientLlamaServerEvaluationProvenance, CancellationToken, Task> bindProvenance,
        Func<TransientLlamaServerEvaluationSession, CancellationToken, Task<T>> body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(bindProvenance);
        ArgumentNullException.ThrowIfNull(body);
        if (!request.LaunchPolicy.IsSupported)
        {
            throw new ArgumentException("The frozen evaluation launch policy is unsupported.", nameof(request));
        }

        var mutationLease = await _supervisor.TryAcquireRuntimeMutationLeaseAsync(ct).ConfigureAwait(false);
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
            variant = binary.Variant;
            var manifest = await _capabilityManifestProbe.GetManifestAsync(binary, ct).ConfigureAwait(false);
            if (!manifest.ProbeSucceeded)
            {
                throw new LlamaRuntimeException("The selected llama.cpp runtime could not report its supported server options.");
            }

            using var loadTicket = variant == GpuVariant.Cpu
                ? null
                : await _loadAdmission.AcquireAsync(ct).ConfigureAwait(false);
            return await _launcher.RunEvaluationAsync(request,
                                      binary,
                                      variant,
                                      manifest,
                                      _launchPolicy,
                                      bindProvenance,
                                      body,
                                      ct)
                                  .ConfigureAwait(false);
        }
    }
}
