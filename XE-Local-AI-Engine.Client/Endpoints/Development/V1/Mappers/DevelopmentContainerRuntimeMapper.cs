namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>Projects the container-runtime preflight onto its wire contract.</summary>
internal static class DevelopmentContainerRuntimeMapper
{
    public static DevelopmentContainerRuntimeResponse ToResponse(this DockerDaemonPreflight preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);

        return new DevelopmentContainerRuntimeResponse
        {
            Ready = preflight.Ready,
            Status = ToStatusCode(preflight.Status),
            Message = preflight.Message,
            RequiresOperatorConfirmation = preflight.RequiresOperatorConfirmation,
            Endpoint = preflight.Endpoint?.Display,
            EndpointSource = preflight.Endpoint is null ? null : ToSourceCode(preflight.Endpoint.Source),
            ObservedDaemon = preflight.ObservedDaemon is null
                ? null
                : new DevelopmentContainerDaemonResponse
                {
                    DaemonId = preflight.ObservedDaemon.DaemonId,
                    ServerVersion = preflight.ObservedDaemon.ServerVersion,
                    Endpoint = preflight.ObservedDaemon.Endpoint.Display,
                    ConfirmedAtUtc = null
                },
            PinnedDaemon = preflight.PinnedDaemon is null
                ? null
                : new DevelopmentContainerDaemonResponse
                {
                    DaemonId = preflight.PinnedDaemon.DaemonId,
                    ServerVersion = preflight.PinnedDaemon.ServerVersion,
                    Endpoint = preflight.PinnedDaemon.Endpoint,
                    ConfirmedAtUtc = preflight.PinnedDaemon.ConfirmedAtUtc
                }
        };
    }

    // Mapped explicitly rather than by ToString(): these codes are a wire contract the React app branches on, and
    // renaming an enum member should not silently change what a client sees.
    private static string ToStatusCode(DockerDaemonPreflightStatus status)
    {
        return status switch
        {
            DockerDaemonPreflightStatus.Ready => "ready",
            DockerDaemonPreflightStatus.DaemonUnreachable => "daemon_unreachable",
            DockerDaemonPreflightStatus.PermissionDenied => "permission_denied",
            DockerDaemonPreflightStatus.ApiVersionTooOld => "api_version_too_old",
            DockerDaemonPreflightStatus.DaemonIdentityChanged => "daemon_changed",
            DockerDaemonPreflightStatus.NotConfigured => "not_configured",
            _ => "probe_failed"
        };
    }

    private static string ToSourceCode(DockerDaemonEndpointSource source)
    {
        return source switch
        {
            DockerDaemonEndpointSource.Configuration => "configuration",
            DockerDaemonEndpointSource.DockerHostEnvironmentVariable => "docker_host",
            DockerDaemonEndpointSource.DefaultUnixSocket => "default_socket",
            DockerDaemonEndpointSource.UserRuntimeUnixSocket => "user_socket",
            DockerDaemonEndpointSource.WindowsNamedPipe => "windows_pipe",
            _ => "unknown"
        };
    }
}
