namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     FastEndpoints handler to ensure a llama.cpp prebuilt binary for a chosen acceleration variant is present
///     (POST model-fit/llamacpp/version). Thin transport over <see cref="ILlamaCppBinaryManager.EnsureBinaryAsync" />:
///     it downloads + hash-verifies the pinned prebuilt for the requested variant (<c>cpu|cuda|vulkan</c>) when missing and
///     returns the resolved binary. There is NO arbitrary version/tag input — the release tag is pinned in code
///     (<see cref="LlamaCppReleasePins" />); this endpoint only selects which acceleration variant to acquire. An unknown
///     variant is rejected with a 400.
/// </summary>
public sealed class EnsureLlamaCppBinaryEndpoint(
    ILlamaCppBinaryManager binaryManager,
    IInstalledRuntimeStore installedRuntimeStore,
    ILlamaCppSourceBuildActivity sourceBuildActivity,
    ILlamaServerProcessSupervisor processSupervisor,
    INodeRuntimeSettings nodeRuntimeSettings,
    ILogger<EnsureLlamaCppBinaryEndpoint> logger) : Endpoint<EnsureLlamaCppBinaryRequest, LlamaCppVersionResponse>
{
    private readonly ILlamaCppBinaryManager _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
    private readonly IInstalledRuntimeStore _installedRuntimeStore = installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));
    private readonly ILogger<EnsureLlamaCppBinaryEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly INodeRuntimeSettings _nodeRuntimeSettings = nodeRuntimeSettings ?? throw new ArgumentNullException(nameof(nodeRuntimeSettings));
    private readonly ILlamaServerProcessSupervisor _processSupervisor = processSupervisor ?? throw new ArgumentNullException(nameof(processSupervisor));
    private readonly ILlamaCppSourceBuildActivity _sourceBuildActivity = sourceBuildActivity ?? throw new ArgumentNullException(nameof(sourceBuildActivity));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.LlamaCppVersion);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<LlamaCppVersionResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces<LlamaCppUpdateBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(EnsureLlamaCppBinaryRequest req, CancellationToken ct)
    {
        if (ModelFitMapper.TryParseVariant(req.Variant) is not { } variant)
        {
            AddError("Variant is not supported.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var (mutationLease, runningProcessCount, blockedMessage) = await LlamaCppPrebuiltRuntimeMutationGuard
                                                                             .TryAcquireAsync(_installedRuntimeStore, _sourceBuildActivity, _processSupervisor, ct)
                                                                             .ConfigureAwait(false);
            await using var ownedMutationLease = mutationLease;
            if (mutationLease is null || blockedMessage is not null)
            {
                await Send.ResultAsync(Results.Conflict(new LlamaCppUpdateBlockedResponse
                {
                    RunningProcessCount = runningProcessCount,
                    Message = blockedMessage ?? "The llama.cpp runtime is busy with another build or runtime change."
                })).ConfigureAwait(false);
                return;
            }

            var binary = await _binaryManager.EnsureBinaryAsync(variant, mutationLease, ct).ConfigureAwait(false);
            var recommendedTag = await _nodeRuntimeSettings.GetRecommendedLlamaCppTagAsync(ct).ConfigureAwait(false);
            await Send.OkAsync(binary.ToResponse(recommendedTag), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LlamaRuntimeException exception)
        {
            // The message is contractually sanitized (no path/URL/secret) — surface it as a 400.
            _logger.LogWarning(exception, "Ensuring the llama.cpp binary for variant {Variant} failed.", variant);
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
