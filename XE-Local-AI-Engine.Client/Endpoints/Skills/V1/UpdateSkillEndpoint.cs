namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class UpdateSkillEndpoint : Endpoint<UpdateSkillRequest, SkillResponse>
{
    private readonly IAgentSkillService _agentSkillService;
    private readonly TimeProvider _timeProvider;

    public UpdateSkillEndpoint(IAgentSkillService agentSkillService, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(agentSkillService);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _agentSkillService = agentSkillService;
        _timeProvider = timeProvider;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Skills.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdateSkillRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The echoed provenance block is operator input like any other field, so it is bounded here, not trusted.
        if (GenerationProvenance.Validate(req.GenerationMetadata) is { } metadataError)
        {
            AddError(metadataError);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var record = await _agentSkillService.UpdateAsync(req.SkillId, req.ToInput(_timeProvider.GetUtcNow()), ct);
        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct);
    }
}
