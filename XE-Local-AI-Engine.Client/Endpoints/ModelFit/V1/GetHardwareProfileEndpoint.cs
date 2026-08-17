namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     FastEndpoints handler for the node hardware profile (GET model-fit/hardware-profile). Returns the sanitized
///     RAM/VRAM/GPU-vendor/CPU/free-disk aggregates the advisor sizes its memory-fit budget against — the PHYSICAL
///     facts — PLUS the runtime device audit: whether the selected inference runtime actually uses the
///     advertised GPU or has silently fallen back to the CPU (inferenceBackend, cpuFallback, reason, remediation).
///     Carries NO machine identifier (hostname/serial) — aggregates only. A <c>?refresh=true</c> query bypasses the
///     in-memory caches and re-probes.
/// </summary>
public sealed class GetHardwareProfileEndpoint(IHardwareProfiler hardwareProfiler, IRuntimeDeviceAudit runtimeAudit)
    : Endpoint<GetHardwareProfileRequest, HardwareProfileResponse>
{
    private readonly IHardwareProfiler _hardwareProfiler = hardwareProfiler ?? throw new ArgumentNullException(nameof(hardwareProfiler));
    private readonly IRuntimeDeviceAudit _runtimeAudit = runtimeAudit ?? throw new ArgumentNullException(nameof(runtimeAudit));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.HardwareProfile);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetHardwareProfileRequest req, CancellationToken ct)
    {
        // The response shows physical hardware (the raw profile) AND runtime truth (the audit): a GPU box whose Vulkan
        // runtime enumerates no devices reports the GPU as present but flags cpuFallback so the UI can surface it.
        var profile = await _hardwareProfiler.GetProfileAsync(req.Refresh, ct).ConfigureAwait(false);
        var audit = await _runtimeAudit.GetAuditAsync(req.Refresh, ct).ConfigureAwait(false);
        await Send.OkAsync(profile.ToResponse(audit), ct).ConfigureAwait(false);
    }
}

/// <summary>Query-string request for <c>GET model-fit/hardware-profile</c>. <see cref="Refresh" /> forces a re-probe.</summary>
public sealed class GetHardwareProfileRequest
{
    /// <summary>When true, bypasses the profiler's in-memory cache and re-probes the hardware.</summary>
    public bool Refresh { get; init; }
}
