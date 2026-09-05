namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class SaveNodeSettingsEndpoint(INodeSettingsAdministrationService administrationService) : Endpoint<SaveNodeSettingsRequest, NodeSettingsResponse>
{
    private readonly INodeSettingsAdministrationService _administrationService = administrationService ?? throw new ArgumentNullException(nameof(administrationService));

    public override void Configure()
    {
        Put(LocalApiRoutes.NodeSettings.Settings);
        Policies(NodeAuthorizationPolicies.Operator);
        // Only the 409 is declared: FastEndpoints already advertises the 200 and, because the host configures
        // Errors.UseProblemDetails(), a ProblemDetails 400. Declaring those explicitly re-labelled the 400 as the
        // FastEndpoints ErrorResponse shape, which is not what this endpoint actually sends.
        Description(builder => builder.Produces<NodeSettingsConflictResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(SaveNodeSettingsRequest req, CancellationToken ct)
    {
        // The merge, not a merged record, and no load of its own: this request is optional field by optional field, so
        // every field it omits is resolved from the record the service applies this to — the one the write lands on.
        // Resolving them from a snapshot loaded here instead wrote that snapshot back over every field a sibling
        // writer had changed while this save validated.
        //
        // Cross-field guards run on the MERGED result in NodeSettingsPolicy — the boundary validator only sees the
        // request, not the current stored state, and some rules need the EFFECTIVE runtime value (stored > appsettings
        // seed > default) for a knob the request omitted. The policy stops at the first violation, matching the
        // one-error-at-a-time response this endpoint has always sent.
        var result = await _administrationService.SaveTrustedMergedAsync(current => req.ToStoredSettings(current), ct).ConfigureAwait(false);
        if (result.Conflicted)
        {
            // Nothing was written: the stored record changed under every validation attempt. Operator-facing, and the
            // same request usually succeeds on a reload.
            await Send.ResultAsync(Results.Conflict(new NodeSettingsConflictResponse
            {
                Message = "Node settings changed while this save was being validated. Reload and retry."
            })).ConfigureAwait(false);
            return;
        }

        if (!result.Updated)
        {
            foreach (var policyError in result.ValidationErrors)
            {
                AddPolicyError(policyError);
            }

            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(result.Settings.ToResponse(), ct).ConfigureAwait(false);
    }

    // Maps a policy violation back onto the request property it belongs to, so the 400 body keeps naming the same
    // field it always has.
    private void AddPolicyError(NodeSettingsValidationError error)
    {
        switch (error.Field)
        {
            case NodeSettingsField.SpeculativeDraftModelName:
                AddError(r => r.SpeculativeDraftModelName, error.Message);
                break;
            case NodeSettingsField.KeepModelWarmModelName:
                AddError(r => r.KeepModelWarmModelName, error.Message);
                break;
            case NodeSettingsField.LlamaMaxLoadedProcesses:
                AddError(r => r.LlamaMaxLoadedProcesses, error.Message);
                break;
            case NodeSettingsField.KeepModelWarmIntervalSeconds:
                AddError(r => r.KeepModelWarmIntervalSeconds, error.Message);
                break;
            case NodeSettingsField.AutoEffortFastModelName:
                AddError(r => r.AutoEffortFastModelName, error.Message);
                break;
            default:
                AddError(error.Message);
                break;
        }
    }
}
