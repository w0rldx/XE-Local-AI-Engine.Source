namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class PreviewGgufImportEndpoint(IGgufImportTransactionCoordinator coordinator)
    : Endpoint<PreviewGgufImportRequest, PreviewGgufImportResponse>, IDesktopOnlyEndpoint
{
    private readonly IGgufImportTransactionCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.ImportPreview);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(PreviewGgufImportRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.SourcePath))
        {
            AddError("An absolute GGUF source path is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var preview = await _coordinator.PreviewAsync(req.SourcePath, ct).ConfigureAwait(false);
            await Send.OkAsync(new PreviewGgufImportResponse
            {
                ModelBaseName = preview.ModelBaseName,
                DetectedQuantization = preview.DetectedQuantization,
                CanonicalQuantizationChoices = preview.CanonicalQuantizationChoices,
                CanonicalModelName = preview.CanonicalModelName,
                FinalFileName = preview.FinalFileName,
                SizeBytes = preview.SizeBytes,
                SourceDisplayName = preview.SourceDisplayName,
                Architecture = preview.Architecture,
                GgufVersion = preview.GgufVersion,
                Warnings = preview.Warnings,
                HasSufficientStorage = preview.HasSufficientStorage,
                PreviewToken = preview.PreviewToken,
                ExpiresAtUtc = preview.ExpiresAtUtc
            }, ct).ConfigureAwait(false);
        }
        catch (GgufImportApplicationException exception)
        {
            await Send.ResultAsync(GgufImportEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}
