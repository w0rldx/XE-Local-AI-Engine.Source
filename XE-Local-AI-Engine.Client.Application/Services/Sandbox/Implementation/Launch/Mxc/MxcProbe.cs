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

            var request = MxcPolicyMapper.BuildProbeRequest();
            MxcPolicyGuard.AssertProbe(request);
            var probe = runtime.Probe(request);
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

            // Win32k is allowed (ADR 0019 amendment), so the clipboard and input-injection denial IS the UI boundary: a host that reports
            // it cannot enforce it is refused. Unreported facts only warn: a Preview mechanism must not switch off on a missing field.
            var ui = probe.Probes?.UiCapabilities;
            if (ui is null)
            {
                return new MxcProbeResult
                {
                    Supported = true,
                    Tier = tier.ToString(),
                    Warnings = [.. warnings, UiFactsNotReportedWarning]
                };
            }

            if (!ui.CanBlockClipboardRead || !ui.CanBlockClipboardWrite || !ui.CanBlockInputInjection)
            {
                return new MxcProbeResult
                {
                    Supported = false,
                    Warnings = warnings,
                    Reason = UiBoundaryUnenforceableReason,
                };
            }

            return new MxcProbeResult
            {
                Supported = true,
                Tier = tier.ToString(),
                Warnings = [.. warnings, .. UiIsolationWarnings(ui)]
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

    internal const string UiFactsNotReportedWarning =
        "MXC did not report its UI capability facts (CanBlockClipboardRead, CanBlockClipboardWrite, CanBlockInputInjection) on this host, "
        + "so whether the sandbox, which allows Win32k, blocks clipboard access and input injection is unverified.";

    internal const string UiBoundaryUnenforceableReason =
        "MXC cannot block clipboard access or input injection on this host, and the sandbox allows Win32k.";

    /// <summary>UI isolation MXC cannot enforce on this host: weaker containment worth naming, not a reason to refuse the boundary.</summary>
    private static IEnumerable<string> UiIsolationWarnings(UiCapabilitySupport ui)
    {
        if (!ui.CanBlockExternalUiObjects)
        {
            yield return UiIsolationWarning(nameof(UiCapabilitySupport.CanBlockExternalUiObjects));
        }

        if (!ui.CanBlockGlobalUiNamespace)
        {
            yield return UiIsolationWarning(nameof(UiCapabilitySupport.CanBlockGlobalUiNamespace));
        }

        if (!ui.CanBlockDesktopSwitching)
        {
            yield return UiIsolationWarning(nameof(UiCapabilitySupport.CanBlockDesktopSwitching));
        }
    }

    private static string UiIsolationWarning(string flag) =>
        $"MXC reports {flag} = false on this host: the sandbox allows Win32k, so this UI isolation is not enforced.";

    private static MxcProbeResult Unavailable(string reason) =>
        new()
        {
            Supported = false,
            Warnings = [],
            Reason = reason
        };
}
