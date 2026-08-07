namespace XE_Local_AI_Engine.Client.Endpoints.CustomTools.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CustomTools;

/// <summary>
///     Authoring-time validation of a candidate command executable for the ProgramLaunch selector (POST
///     custom-tools/executable-probe). Runs the same <c>HostExecutableGuard</c> checks the executor runs at launch —
///     absolute path, not a shell/interpreter/script, a real regular file (O_NOFOLLOW, no symlink) — and reports
///     ok/reason. This is not a filesystem browser: it validates one path the UI supplies. Desktop-only (a headless
///     host has no operator picking a local binary) and Operator-gated.
/// </summary>
public sealed class ValidateExecutableEndpoint(ICustomToolService customToolService)
    : Endpoint<ProbeExecutableRequest, HostExecutableProbeResult>, IDesktopOnlyEndpoint
{
    private readonly ICustomToolService _customToolService = customToolService ?? throw new ArgumentNullException(nameof(customToolService));

    public override void Configure()
    {
        Post(LocalApiRoutes.CustomTools.ExecutableProbe);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ProbeExecutableRequest req, CancellationToken ct)
    {
        var result = _customToolService.ProbeExecutable(req.Path);
        await Send.OkAsync(result, ct).ConfigureAwait(false);
    }
}
