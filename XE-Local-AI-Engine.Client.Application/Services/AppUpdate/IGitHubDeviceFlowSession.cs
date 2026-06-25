namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     Holds the in-flight device-flow <c>device_code</c> server-side so the <c>poll</c> endpoint can replay it WITHOUT
///     React ever seeing it (sec H4 — the device_code is a secret polling credential). A single volatile slot: the
///     <c>start</c> endpoint stores the device code returned by <see cref="IGitHubAuthService.StartAsync" />; <c>poll</c>
///     reads it; a successful authorize / sign-out clears it. Registered as a singleton; the loopback host serves one
///     operator, so a single pending flow is sufficient.
/// </summary>
public interface IGitHubDeviceFlowSession
{
    /// <summary>The pending device code, or <see langword="null" /> when no flow is in progress.</summary>
    string? PendingDeviceCode { get; }

    /// <summary>Stores the device code of a newly-started flow (replaces any prior pending flow).</summary>
    void Begin(string deviceCode);

    /// <summary>Clears the pending device code (after authorize / denial / expiry / sign-out).</summary>
    void Clear();
}

/// <summary>Default in-memory <see cref="IGitHubDeviceFlowSession" /> — a single volatile slot, lock-free reads/writes.</summary>
public sealed class GitHubDeviceFlowSession : IGitHubDeviceFlowSession
{
    private string? _pendingDeviceCode;

    public string? PendingDeviceCode => Volatile.Read(ref _pendingDeviceCode);

    public void Begin(string deviceCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceCode);
        Volatile.Write(ref _pendingDeviceCode, deviceCode);
    }

    public void Clear()
    {
        Volatile.Write(ref _pendingDeviceCode, value: null);
    }
}
