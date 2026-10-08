namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

using Microsoft.Mxc.Sdk.V1;

/// <summary>Measures MXC ProcessContainer availability without launching anything. Never throws.</summary>
/// <remarks>
///     Availability is what MXC itself reports: <c>GetPlatformSupport()</c>, then <c>Probe</c> on the exact policy the engine uses.
///     No OS build floor is hardcoded here; the SDK's runtime probe is the source of truth.
/// </remarks>
public static class MxcProbe
{
    public static MxcProbeResult Measure(IMxcSandboxRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        try
        {
            var platform = runtime.GetPlatformSupport();
            if (!platform.IsSupported)
            {
                return Unavailable($"MXC reports this platform unsupported: {platform.Reason ?? "no reason given"}.");
            }

            if (platform.AvailableMethods?.Contains(ContainmentBackend.ProcessContainer) != true)
            {
                return Unavailable("MXC reports no ProcessContainer backend on this host.");
            }

            var probe = runtime.Probe(MxcPolicyMapper.BuildProbeRequest());
            var warnings = probe.Warnings ?? [];
            if (probe.Error is not null || probe.Tier is not { } tier)
            {
                return new MxcProbeResult
                {
                    Supported = false,
                    Warnings = warnings,
                    Reason = $"MXC rejected the engine's ProcessContainer policy: {probe.Error ?? "no tier reported"}.",
                };
            }

            return new MxcProbeResult
            {
                Supported = true,
                Tier = tier.ToString(),
                Warnings = warnings
            };
        }
        catch (MxcException exception)
        {
            return Unavailable($"MXC probe failed ({exception.Code}): {exception.Message}");
        }
        catch (DllNotFoundException exception)
        {
            return Unavailable($"The MXC native library could not be loaded: {exception.Message}");
        }
        catch (Exception exception)
        {
            // A probe must degrade to "unavailable", never take the host down: anything else the native boundary throws lands here.
            return Unavailable($"MXC probe failed ({exception.GetType().Name}): {exception.Message}");
        }
    }

    private static MxcProbeResult Unavailable(string reason) =>
        new()
        {
            Supported = false,
            Warnings = [],
            Reason = reason
        };
}
