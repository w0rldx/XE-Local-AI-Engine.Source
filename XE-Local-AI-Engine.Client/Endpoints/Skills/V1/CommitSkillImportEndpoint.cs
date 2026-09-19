namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Phase 2 of the third-party skill import: persists the skills the operator selected from a preview they
///     acknowledged. Writes the materialised preview payload verbatim — the source is never re-parsed or re-fetched, so
///     what lands is what was reviewed even if the repository changed in between.
/// </summary>
/// <remarks>
///     Without <c>acknowledged: true</c> the import service refuses before it even looks the token up, so an
///     unacknowledged call cannot consume a preview either. Imported skills land disabled with Imported provenance;
///     that, not the acknowledgement, is the control that keeps third-party instructions away from a model.
/// </remarks>
public sealed class CommitSkillImportEndpoint : Endpoint<SkillImportCommitEndpointRequest, SkillImportCommitResponse>
{
    private readonly ISkillImportService _importService;

    public CommitSkillImportEndpoint(ISkillImportService importService)
    {
        ArgumentNullException.ThrowIfNull(importService);
        _importService = importService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Skills.Import);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SkillImportCommitEndpointRequest req, CancellationToken ct)
    {
        var result = await _importService.CommitAsync(new SkillImportCommitRequest
        {
            Token = req.Token,
            SkillNames = req.SkillNames ?? [],
            ConflictResolution = req.ConflictResolution,
            Acknowledged = req.Acknowledged
        },
            ct);

        await Send.OkAsync(result.ToResponse(), ct);
    }
}
