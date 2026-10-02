namespace XE_Local_AI_Engine.Client.Endpoints.Diagnostics.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Diagnostics.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Diagnostics;

/// <summary>The node-info report: what a bug report needs about this node, with no host name, user name or path.</summary>
public sealed class GetNodeInfoEndpoint : EndpointWithoutRequest<NodeInfoResponse>
{
    private readonly AppUpdateHostContext _hostContext;
    private readonly INodeInfoService _nodeInfo;

    public GetNodeInfoEndpoint(INodeInfoService nodeInfo, AppUpdateHostContext hostContext)
    {
        ArgumentNullException.ThrowIfNull(nodeInfo);
        ArgumentNullException.ThrowIfNull(hostContext);
        _nodeInfo = nodeInfo;
        _hostContext = hostContext;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Diagnostics.NodeInfo);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var info = await _nodeInfo.GetAsync(_hostContext.IsShellOwned, ct);
        await Send.OkAsync(info.ToResponse(), ct);
    }
}
