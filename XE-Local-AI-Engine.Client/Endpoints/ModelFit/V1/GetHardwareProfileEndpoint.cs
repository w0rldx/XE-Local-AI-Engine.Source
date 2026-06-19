namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     FastEndpoints handler for the node hardware profile (GET model-fit/hardware-profile). Thin transport over the
///     <see cref="IHardwareProfiler" />: it returns the sanitized RAM/VRAM/GPU-vendor/CPU/free-disk aggregates the
///     advisor sizes its memory-fit budget against. Carries NO machine identifier (hostname/serial) — aggregates only.
///     A <c>?refresh=true</c> query bypasses the in-memory cache and re-probes.
/// </summary>
public sealed class GetHardwareProfileEndpoint(IHardwareProfiler hardwareProfiler)
    : Endpoint<GetHardwareProfileRequest, HardwareProfileResponse>
{
    private readonly IHardwareProfiler _hardwareProfiler = hardwareProfiler ?? throw new ArgumentNullException(nameof(hardwareProfiler));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.HardwareProfile);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetHardwareProfileRequest req, CancellationToken ct)
    {
        var profile = await _hardwareProfiler.GetProfileAsync(req.Refresh, ct).ConfigureAwait(false);
        await Send.OkAsync(profile.ToResponse(), ct).ConfigureAwait(false);
    }
}

/// <summary>Query-string request for <c>GET model-fit/hardware-profile</c>. <see cref="Refresh" /> forces a re-probe.</summary>
public sealed class GetHardwareProfileRequest
{
    /// <summary>When true, bypasses the profiler's in-memory cache and re-probes the hardware.</summary>
    public bool Refresh { get; init; }
}
