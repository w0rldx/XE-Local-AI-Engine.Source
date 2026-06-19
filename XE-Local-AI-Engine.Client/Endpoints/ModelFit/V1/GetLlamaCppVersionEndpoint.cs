namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     FastEndpoints handler for the resolved llama.cpp binary version (GET model-fit/llamacpp/version). The
///     <see cref="ILlamaCppBinaryManager" /> exposes ONLY <c>EnsureBinaryAsync(variant)</c> — there is no source-build and
///     no arbitrary version/tag input (the recommended release tag is pinned in code, <see cref="LlamaCppReleasePins" />).
///     So this read selects the recommended host variant via <see cref="IGpuVariantSelector" /> and resolves/ensures that
///     pinned prebuilt, returning its tag + variant + pinned-fallback flag + the recommended pinned tag.
///     <para>
///         <b>Honest limit:</b> resolving the binary is idempotent (a cached, hash-valid binary is reused without
///         re-download), but on a fresh node this GET may trigger the first prebuilt download. There is no read-only
///         "current resolved binary without ensuring" seam on the binary manager.
///     </para>
/// </summary>
public sealed class GetLlamaCppVersionEndpoint(
    ILlamaCppBinaryManager binaryManager,
    IGpuVariantSelector variantSelector,
    ILogger<GetLlamaCppVersionEndpoint> logger) : EndpointWithoutRequest<LlamaCppVersionResponse>
{
    private readonly ILlamaCppBinaryManager _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
    private readonly ILogger<GetLlamaCppVersionEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IGpuVariantSelector _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.LlamaCppVersion);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
            var binary = await _binaryManager.EnsureBinaryAsync(variant, ct).ConfigureAwait(false);
            await Send.OkAsync(binary.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LlamaRuntimeException exception)
        {
            // The message is contractually sanitized (no path/URL/secret) — surface it as a 400 so the version panel can
            // show why the binary could not be resolved (e.g. no prebuilt for the host, repeated hash mismatch).
            _logger.LogWarning(exception, "Resolving the llama.cpp binary version failed.");
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
