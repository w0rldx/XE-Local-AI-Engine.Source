namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Returns one bundled file including its decrypted content. The resource name is a skill-root-relative path, so it
///     reaches the endpoint percent-escaped and is decoded and charset-validated before the lookup.
/// </summary>
public sealed class GetSkillResourceEndpoint : Endpoint<GetSkillResourceRequest, SkillResourceResponse>
{
    private readonly IAgentSkillService _agentSkillService;

    public GetSkillResourceEndpoint(IAgentSkillService agentSkillService)
    {
        ArgumentNullException.ThrowIfNull(agentSkillService);
        _agentSkillService = agentSkillService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Skills.ResourceByName);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetSkillResourceRequest req, CancellationToken ct)
    {
        var name = SkillResourceRouteName.DecodeAndValidate(req.ResourceName);
        if (name is null)
        {
            AddError("The resource name is invalid.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var record = await _agentSkillService.GetByIdAsync(req.SkillId, ct);

        // Case-insensitive, matching the store's upsert-by-name rule: a name that would REPLACE a resource on write
        // has to be the name that FINDS it on read.
        var resource = (record?.Resources ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

        if (resource is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(resource.ToResponse(), ct);
    }
}
