namespace XE_Local_AI_Engine.Client.Services.CustomTools;

using System.Net;
using System.Net.Sockets;

/// <summary>
///     The SSRF containment for <c>HttpFetch</c> tools (C4/H1/H2). Two layers:
///     <list type="bullet">
///         <item>
///             <see cref="ValidateRequestUrl" /> runs on the FINAL assembled URL before the request is built: scheme
///             allow-list, no userinfo, no numeric/encoded host literal, and — when the template host is parameterized —
///             a mandatory <c>allowedHosts</c> membership check. It rejects a literal IP that falls in any private,
///             loopback, link-local, CGNAT, metadata, or reserved range.
///         </item>
///         <item>
///             <see cref="CreatePinnedConnectCallback" /> is installed on the fetch handler's
///             <see cref="SocketsHttpHandler" />: it resolves the host, validates EVERY resolved address, and connects
///             the socket to a validated address itself. Because the address it validates is the address it dials, there
///             is no re-resolve gap a DNS-rebind could exploit — the TOCTOU window C4 warns about is closed. The original
///             host stays the connection's Host header + TLS SNI (the handler layers TLS over the returned stream).
///         </item>
///     </list>
/// </summary>
internal static class CustomToolSsrfGuard
{
    /// <summary>
    ///     Validates the final assembled request URL. Throws <see cref="CustomToolExecutionException" /> on any violation.
    ///     DNS resolution is deliberately NOT done here — it happens in the pinned connect callback so the validated
    ///     address is the dialed address; this method covers everything decidable from the URL alone.
    /// </summary>
    public static void ValidateRequestUrl(Uri url, IReadOnlyList<string> allowedHosts, bool hostIsParameterized)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(allowedHosts);

        if (!url.IsAbsoluteUri)
        {
            throw new CustomToolExecutionException("The request URL must be absolute.");
        }

        if (!string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new CustomToolExecutionException("The request URL must use the http or https scheme.");
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            throw new CustomToolExecutionException("The request URL must not contain userinfo (credentials in the URL).");
        }

        var host = url.Host.Trim('[', ']');
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new CustomToolExecutionException("The request URL has no host.");
        }

        if (LooksLikeNumericHostLiteral(host) && !IPAddress.TryParse(host, out _))
        {
            // A decimal (http://2130706433), octal (http://0177.0.0.1), or hex (http://0x7f.0.0.1) host would be
            // re-decoded to a private/loopback address downstream; only a canonical dotted/colon literal is allowed.
            throw new CustomToolExecutionException("The request URL host is a non-canonical numeric literal and is rejected.");
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            if (IsDeniedAddress(literal))
            {
                throw new CustomToolExecutionException("The request URL targets a private, loopback, link-local, or otherwise reserved address.");
            }
        }
        else
        {
            // A DNS name. If the operator let the model fill the host, an allow-list is mandatory — otherwise the model
            // could point the fetch anywhere. Even when the host is fixed, honor a configured allow-list as defense.
            if (hostIsParameterized && allowedHosts.Count == 0)
            {
                throw new CustomToolExecutionException("A tool whose URL host is parameterized must declare allowedHosts.");
            }

            if (allowedHosts.Count > 0
                && !allowedHosts.Any(allowed => string.Equals(allowed.Trim().Trim('[', ']'), host, StringComparison.OrdinalIgnoreCase)))
            {
                throw new CustomToolExecutionException($"The request host '{host}' is not in the tool's allowedHosts.");
            }
        }
    }

    /// <summary>
    ///     Builds the <see cref="SocketsHttpHandler.ConnectCallback" /> that pins the connection to a validated address.
    ///     Shared by every custom-tool fetch (registered once on the named client), so it is stateless.
    /// </summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> CreatePinnedConnectCallback()
    {
        return async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;

            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal))
            {
                addresses = [literal];
            }
            else
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }

            if (addresses.Length == 0)
            {
                throw new CustomToolExecutionException($"The host '{host}' did not resolve to any address.");
            }

            // Reject if ANY resolved address is denied: a rebind that mixes a public decoy with a private target must not
            // be able to have us dial the private one, and the caller cannot know which record the stack would pick.
            if (addresses.Any(IsDeniedAddress))
            {
                throw new CustomToolExecutionException("The host resolved to a private, loopback, link-local, or otherwise reserved address.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    /// <summary>
    ///     True when <paramref name="address" /> falls in any range a custom-tool fetch must never reach: loopback,
    ///     RFC 1918 private, link-local (incl. the 169.254.169.254 cloud-metadata address), CGNAT, IETF-reserved,
    ///     broadcast/multicast, and the IPv6 equivalents (ULA, link-local, site-local, multicast, NAT64, unspecified).
    ///     IPv4-mapped IPv6 addresses are unwrapped and re-tested as IPv4, and so is the deprecated IPv4-compatible form
    ///     (<c>::a.b.c.d</c>, e.g. <c>::169.254.169.254</c>) — otherwise it would sail past every IPv4 check below and
    ///     reach the metadata/loopback range it embeds.
    /// </summary>
    public static bool IsDeniedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            return IsDeniedAddress(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            var highBitsZero = bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0;
            if (highBitsZero && (bytes[12] | bytes[13] | bytes[14] | bytes[15]) != 0)
            {
                // ::/96 with a nonzero low 32 bits is the deprecated IPv4-compatible form; the all-zero low bits case
                // (the IPv6 unspecified address, ::) is excluded by the != 0 guard and falls through to IsDeniedIPv6.
                return IsDeniedAddress(new IPAddress(bytes.AsSpan(12, 4)));
            }
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsDeniedIPv4(address),
            AddressFamily.InterNetworkV6 => IsDeniedIPv6(address),
            _ => true
        };
    }

    private static bool IsDeniedIPv4(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true; // 127.0.0.0/8
        }

        var b = address.GetAddressBytes();
        return b[0] == 0                                          // 0.0.0.0/8 (incl. unspecified)
               || b[0] == 10                                      // 10.0.0.0/8
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)       // 172.16.0.0/12
               || (b[0] == 192 && b[1] == 168)                    // 192.168.0.0/16
               || (b[0] == 169 && b[1] == 254)                    // 169.254.0.0/16 (incl. 169.254.169.254 metadata)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)      // 100.64.0.0/10 (CGNAT)
               || (b[0] == 192 && b[1] == 0 && b[2] == 0)         // 192.0.0.0/24
               || (b[0] == 198 && (b[1] == 18 || b[1] == 19))     // 198.18.0.0/15 (benchmarking)
               || b[0] >= 224;                                    // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved + 255.255.255.255
    }

    private static bool IsDeniedIPv6(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6Multicast)
        {
            return true; // ::1, ff00::/8
        }

        var b = address.GetAddressBytes();

        if (IsAllZero(b))
        {
            return true; // :: unspecified
        }

        if ((b[0] & 0xFE) == 0xFC)
        {
            return true; // fc00::/7 unique-local
        }

        if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)
        {
            return true; // fe80::/10 link-local
        }

        if (b[0] == 0xFE && (b[1] & 0xC0) == 0xC0)
        {
            return true; // fec0::/10 deprecated site-local
        }

        // 64:ff9b::/96 NAT64 — the low 32 bits embed an IPv4 address, so treat the whole prefix as reachable-through.
        return b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B
               && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0
               && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0;
    }

    private static bool IsAllZero(byte[] bytes)
    {
        return bytes.All(static value => value == 0);
    }

    private static bool LooksLikeNumericHostLiteral(string host)
    {
        // A canonical IPv6 literal contains ':' and is handled by IPAddress.TryParse; flag the IPv4-style encodings a
        // rebind uses to smuggle a private address past a dotted-quad check: an all-digit host, a hex host, or a dotted
        // host with a leading-zero (octal) or 0x (hex) label.
        if (host.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        if (host.Contains("0x", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A leading-zero label (e.g. 0177) is an octal encoding; a fully numeric host is a bare decimal (e.g. 2130706433).
        if (host.Split('.').Any(static label => label.Length > 1 && label[0] == '0'))
        {
            return true;
        }

        return host.All(char.IsAsciiDigit);
    }
}
