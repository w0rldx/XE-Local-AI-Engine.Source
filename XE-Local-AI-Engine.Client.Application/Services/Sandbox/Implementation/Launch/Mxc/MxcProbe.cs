namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

using System.Globalization;
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
                    UiFacts = ui
                };
            }

            return new MxcProbeResult
            {
                Supported = true,
                Tier = tier.ToString(),
                Warnings = [.. warnings, .. UiIsolationWarnings(ui)],
                UiFacts = ui
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
        + "so whether the sandbox, which allows the Windows UI kernel interface, blocks clipboard access and input injection is unverified.";

    internal const string UiBoundaryUnenforceableReason =
        "MXC cannot block clipboard access or input injection on this host, and the sandbox relies on that denial to contain its use of the Windows UI kernel interface.";

    /// <summary>The six UI facts the boundary decision reads, for the containment log line: <c>true</c>, <c>false</c> or <c>not reported</c>.</summary>
    internal static string DescribeUiFacts(UiCapabilitySupport? ui)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"CanBlockClipboardRead {Fact(ui?.CanBlockClipboardRead)}, CanBlockClipboardWrite {Fact(ui?.CanBlockClipboardWrite)}, CanBlockInputInjection {Fact(ui?.CanBlockInputInjection)}, CanBlockExternalUiObjects {Fact(ui?.CanBlockExternalUiObjects)}, CanBlockGlobalUiNamespace {Fact(ui?.CanBlockGlobalUiNamespace)}, CanBlockDesktopSwitching {Fact(ui?.CanBlockDesktopSwitching)}");

        static string Fact(bool? value) =>
            value switch
            {
                true => "true",
                false => "false",
                null => "not reported"
            };
    }

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
        $"MXC reports {flag} = false on this host: the sandbox allows the Windows UI kernel interface, so this UI isolation is not enforced.";

    private static MxcProbeResult Unavailable(string reason) =>
        new()
        {
            Supported = false,
            Warnings = [],
            Reason = reason
        };
}
