namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Re-probes the runtime and, when the body names the daemon that is answering RIGHT NOW, approves the identity
///     change and re-runs the startup reconciler.
/// </summary>
/// <remarks>
///     <c>acknowledgeDaemonId</c> is never a blind approval: the endpoint re-probes first and compares the id against
///     the observed one, answering 400 on a mismatch — the hole a bare boolean leaves open. Omitting it re-probes and
///     confirms nothing. A POST rather than a GET for the same reason, and the reconcile it runs afterwards:
///     docs/wiki/09-api-and-hubs.md ("Design notes on the newer endpoint families").
/// </remarks>
public sealed class RefreshExternalAppRuntimeEndpoint : Endpoint<RefreshExternalAppRuntimeRequest, ExternalAppRuntimeResponse>
{
    private readonly IExternalAppStartupReconciler _reconciler;

    private readonly IContainerRuntimeResolver _resolver;

    public RefreshExternalAppRuntimeEndpoint(IContainerRuntimeResolver resolver, IExternalAppStartupReconciler reconciler)
    {
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(resolver);
        _reconciler = reconciler;
        _resolver = resolver;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ExternalApps.RuntimeRefresh);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.ProducesProblemDetails()
                                             .ProducesProblemDetails(StatusCodes.Status503ServiceUnavailable));
    }

    public override async Task HandleAsync(RefreshExternalAppRuntimeRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var resolution = await _resolver.ResolveAsync(forceRefresh: true, cancellationToken: ct);

        if (req.AcknowledgeDaemonId is { Length: > 0 } acknowledged)
        {
            if (!string.Equals(acknowledged, resolution.Daemon.DaemonId, StringComparison.Ordinal))
            {
                AddError("That is not the daemon answering now. Re-read the runtime panel before approving it.");
                await Send.ErrorsAsync(cancellation: ct);
                return;
            }

            resolution = await _resolver.ConfirmDaemonIdentityAsync(acknowledged, ct);
        }

        var foreignInstallContainers = 0;
        if (resolution.Ready)
        {
            var summary = await _reconciler.ReconcileAsync(ct);
            foreignInstallContainers = summary.ForeignInstallContainers;
        }

        await Send.OkAsync(ExternalAppMapper.ToRuntimeResponse(resolution, foreignInstallContainers), ct);
    }
}
