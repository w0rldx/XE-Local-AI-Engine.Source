namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Validation;

public sealed class PullLocalModelEndpoint(
    IOllamaModelService modelService,
    ModelNameValidator modelNameValidator) : Endpoint<PullLocalModelRequest, PullLocalModelResponse>
{
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));
    private readonly IOllamaModelService _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalModels.Pull);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(PullLocalModelRequest req, CancellationToken ct)
    {
        if (!await ValidateModelNameAsync(req.ModelName, ct).ConfigureAwait(false))
        {
            return;
        }

        var modelName = req.ModelName!.Trim();
        var status = "Complete";
        long? totalBytes = null;
        long? completedBytes = null;

        await foreach (var progress in _modelService.PullModelAsync(modelName, ct).ConfigureAwait(false))
        {
            status = string.IsNullOrWhiteSpace(progress.Status) ? status : progress.Status;
            totalBytes = progress.Total;
            completedBytes = progress.Completed;
        }

        await Send.OkAsync(new PullLocalModelResponse
        {
            ModelName = modelName,
            Status = status,
            TotalBytes = totalBytes,
            CompletedBytes = completedBytes
        }, ct).ConfigureAwait(false);
    }

    private async Task<bool> ValidateModelNameAsync(string? modelName, CancellationToken ct)
    {
        var validationError = _modelNameValidator.GetValidationError(modelName);
        if (validationError is null)
        {
            return true;
        }

        AddError(validationError);
        await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        return false;
    }
}
