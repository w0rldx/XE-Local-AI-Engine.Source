namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

/// <summary>
///     HTTPS by default for external connections: a plain-http address that is not loopback needs the connection's
///     explicit <c>AllowInsecureHttp</c> opt-in to be saved or probed.
/// </summary>
/// <remarks>
///     Enforced on the store's save path and the probe, never in the address normalizer, which also runs at send time:
///     a connection saved over http before this rule keeps working and is only flagged. Loopback is the one exemption
///     because private-ness of a LAN host needs DNS (rebindable, and checked at save, not at send), and a LAN or VPN
///     segment is exactly where a plaintext Bearer key is sniffable.
/// </remarks>
public static class ExternalProviderTransportPolicy
{
    /// <summary>The operator-facing refusal, naming the opt-in.</summary>
    public const string InsecureRemoteError =
        "A plain-http address that is not on this machine sends the API key and prompts unencrypted. Use https, or allow insecure HTTP for this connection.";

    /// <summary>True for an <c>http</c> address whose host is not loopback (<c>localhost</c>, 127.0.0.0/8, <c>::1</c>).</summary>
    public static bool IsInsecureRemote(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !address.IsLoopback;
    }

    /// <summary>The stored-string form for a read model; an unparseable address is not flagged (it cannot be sent to).</summary>
    public static bool IsInsecureRemote(string? address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && IsInsecureRemote(uri);
    }
}
