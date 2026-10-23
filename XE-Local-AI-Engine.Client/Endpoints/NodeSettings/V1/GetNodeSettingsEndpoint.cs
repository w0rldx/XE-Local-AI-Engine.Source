namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class GetNodeSettingsEndpoint : EndpointWithoutRequest<NodeSettingsResponse>
{
    private readonly INodeSettingsAdministrationService _administrationService;
    private readonly PendingRestartRegistry _pendingRestart;
    private readonly INodeRuntimeSettings _runtimeSettings;

    public GetNodeSettingsEndpoint(INodeSettingsAdministrationService administrationService,
        INodeRuntimeSettings runtimeSettings,
        PendingRestartRegistry pendingRestart)
    {
        ArgumentNullException.ThrowIfNull(administrationService);
        _administrationService = administrationService;
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _pendingRestart = pendingRestart ?? throw new ArgumentNullException(nameof(pendingRestart));
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.NodeSettings.Settings);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var settings = await _administrationService.GetTrustedSettingsAsync(ct);
        await Send.OkAsync(settings.ToResponse(_runtimeSettings.ResolveEffectiveValues(settings), _pendingRestart), ct);
    }
}
