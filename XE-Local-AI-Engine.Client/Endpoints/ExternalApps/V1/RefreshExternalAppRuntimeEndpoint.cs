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
///     <para>
///         <c>acknowledgeDaemonId</c> is never a blind approval. The endpoint re-probes first and compares the id
///         against the observed one, answering 400 on a mismatch, so a client echoing the last id it rendered cannot
///         approve whatever daemon is answering now — the hole a bare boolean leaves open. Omitting it re-probes and
///         confirms nothing.
///     </para>
///     <para>
///         A POST rather than a GET for the same reason: a refresh, a prefetch or a health check must not be able to
///         pin a daemon. After a preflight that comes back ready it reconciles, making "approve the daemon and recover
///         the rows still transient after boot" one operator action rather than a restart; the reconciler skips any
///         instance with an operation in flight, so a refresh during an install cannot disturb it.
///     </para>
/// </summary>
public sealed class RefreshExternalAppRuntimeEndpoint(IContainerRuntimeResolver resolver, IExternalAppStartupReconciler reconciler)
    : Endpoint<RefreshExternalAppRuntimeRequest, ExternalAppRuntimeResponse>
{
    private readonly IExternalAppStartupReconciler _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));

    private readonly IContainerRuntimeResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

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
