namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

/// <summary>Query-string request for <c>GET model-fit/hardware-profile</c>. <see cref="Refresh" /> forces a re-probe.</summary>
public sealed class GetHardwareProfileRequest
{
    /// <summary>When true, bypasses the profiler's in-memory cache and re-probes the hardware.</summary>
    public bool Refresh { get; init; }
}
