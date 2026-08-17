namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <inheritdoc />
internal sealed class TransientLlamaServerEvaluationHarness(
    ILlamaServerProcessSupervisor supervisor,
    ILlamaCppBinaryManager binaryManager,
    IGpuVariantSelector variantSelector,
    ILlamaServerCapabilityManifestProbe capabilityManifestProbe,
    ILlamaServerLaunchPolicy launchPolicy,
    TransientLlamaServerLauncher launcher,
    IGpuModelLoadAdmission loadAdmission) : ITransientLlamaServerEvaluationHarness
{
    private readonly ILlamaCppBinaryManager _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
    private readonly ILlamaServerCapabilityManifestProbe _capabilityManifestProbe =
        capabilityManifestProbe ?? throw new ArgumentNullException(nameof(capabilityManifestProbe));
    private readonly TransientLlamaServerLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    private readonly ILlamaServerLaunchPolicy _launchPolicy = launchPolicy ?? throw new ArgumentNullException(nameof(launchPolicy));
    private readonly IGpuModelLoadAdmission _loadAdmission = loadAdmission ?? throw new ArgumentNullException(nameof(loadAdmission));
    private readonly ILlamaServerProcessSupervisor _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
    private readonly IGpuVariantSelector _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));

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
