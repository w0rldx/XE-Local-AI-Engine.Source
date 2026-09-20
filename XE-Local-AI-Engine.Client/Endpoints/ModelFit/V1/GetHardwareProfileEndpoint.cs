namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     FastEndpoints handler for the node hardware profile (GET model-fit/hardware-profile): the sanitized
///     RAM/VRAM/GPU-vendor/CPU/free-disk aggregates the advisor sizes its memory-fit budget against.
/// </summary>
/// <remarks>
///     Those are the PHYSICAL facts; the response also carries the runtime device audit — whether the selected
///     inference runtime actually uses the advertised GPU or has silently fallen back to the CPU (inferenceBackend,
///     cpuFallback, reason, remediation). It carries NO machine identifier (hostname/serial), aggregates only, and a
///     <c>?refresh=true</c> query bypasses the in-memory caches and re-probes.
/// </remarks>
public sealed class GetHardwareProfileEndpoint : Endpoint<GetHardwareProfileRequest, HardwareProfileResponse>
{
    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly IRuntimeDeviceAudit _runtimeAudit;

    public GetHardwareProfileEndpoint(IHardwareProfiler hardwareProfiler, IRuntimeDeviceAudit runtimeAudit)
    {
        ArgumentNullException.ThrowIfNull(hardwareProfiler);
        ArgumentNullException.ThrowIfNull(runtimeAudit);
        _hardwareProfiler = hardwareProfiler;
        _runtimeAudit = runtimeAudit;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.HardwareProfile);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetHardwareProfileRequest req, CancellationToken ct)
    {
        // The response shows physical hardware (the raw profile) AND runtime truth (the audit): a GPU box whose Vulkan
        // runtime enumerates no devices reports the GPU as present but flags cpuFallback so the UI can surface it.
        var profile = await _hardwareProfiler.GetProfileAsync(req.Refresh, ct);
        var audit = await _runtimeAudit.GetAuditAsync(req.Refresh, ct);
        await Send.OkAsync(profile.ToResponse(audit), ct);
    }
}

/// <summary>Query-string request for <c>GET model-fit/hardware-profile</c>. <see cref="Refresh" /> forces a re-probe.</summary>
public sealed class GetHardwareProfileRequest
{
    /// <summary>When true, bypasses the profiler's in-memory cache and re-probes the hardware.</summary>
    public bool Refresh { get; init; }
}
