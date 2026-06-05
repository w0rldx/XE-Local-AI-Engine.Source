namespace XE_Local_AI_Engine.Tests.Mcp;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Security guard for the HTTP MCP loopback allowlist. The factory rejects any HTTP URL
///     whose host is not in the explicit loopback allowlist BEFORE attempting any connection, so a registration that
///     somehow carries a non-loopback or host-confusion URL can never cause an outbound request to a remote server. The
///     allowlist is strict (exact-string, fail-closed) by design; these cases pin that no SSRF/parser-confusion vector
///     slips a non-loopback host through as one of the allowed strings.
/// </summary>
public sealed class McpClientFactoryLoopbackTests
{
    [Test]
    [Arguments("http://evil.com/mcp")]
    [Arguments("http://169.254.169.254/latest/meta-data/")] // cloud metadata SSRF
    [Arguments("http://10.0.0.5/mcp")] // private range
    [Arguments("http://192.168.1.10/mcp")] // private range
    [Arguments("http://127.0.0.1.evil.com/mcp")] // suffix-confusion: host is 127.0.0.1.evil.com
    [Arguments("http://localhost.evil.com/mcp")] // suffix-confusion
    [Arguments("http://127.0.0.1@evil.com/mcp")] // userinfo trick: host is evil.com
    [Arguments("http://localhost@evil.com/mcp")] // userinfo trick
    [Arguments("http://127.0.0.2/mcp")] // rest of 127/8 is fail-closed (allowlist only has 127.0.0.1)
    [Arguments("http://localhost./mcp")] // trailing-dot host, fail-closed
    [Arguments("http://0.0.0.0/mcp")] // wildcard bind address is not a loopback host
    [Arguments("ftp://127.0.0.1/mcp")] // loopback host but non-HTTP scheme — rejected by the scheme guard
    [Arguments("file:///etc/passwd")] // file scheme — rejected by the scheme guard
    [Arguments("not-a-url")] // unparseable (not an absolute URI)
    [Arguments("")] // empty
    public async Task CreateAsync_WhenHttpUrlIsNotLoopbackOrMalformed_RejectsBeforeConnecting(string url)
    {
        var factory = CreateFactory();
        var record = HttpRecord(url);

        // The non-loopback/host-confusion/malformed URL must be rejected synchronously (before McpClient.CreateAsync is
        // ever reached), so no outbound connection to a remote server can occur.
        await AssertThrowsInvalidOperationAsync(() => factory.CreateAsync(record, CancellationToken.None));
    }

    [Test]
    public async Task CreateAsync_WhenHttpUrlIsBracketedIpv6Loopback_PassesTheLoopbackGuard()
    {
        // LOW-3: Uri.Host returns "[::1]" WITH brackets but the allowlist stores "::1"; the factory trims the brackets,
        // so a valid IPv6 loopback the front-end accepts is NOT rejected here. We cannot assert a successful connection
        // (nothing is listening), but we CAN assert the loopback guard did not fire: any failure must be a transport
        // error, never the "must target a loopback host" rejection.
        var factory = CreateFactory();
        var record = HttpRecord("http://[::1]:65000/mcp");

        // Bound the call: nothing is listening, so a connect attempt should refuse fast, but a deadline guarantees the
        // test can never hang if the SDK parks on the socket.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await factory.CreateAsync(record, cts.Token);
        }
        catch (Exception ex)
        {
            AssertEx.False(IsGuardRejection(ex),
                $"[::1] is a valid loopback host and must pass the loopback/scheme guard; got guard rejection: {ex.Message}");
        }
    }

    private static async Task AssertThrowsInvalidOperationAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException ex) when (IsGuardRejection(ex))
        {
            return;
        }

        throw new AssertionException("Expected the non-loopback / malformed HTTP MCP URL to be rejected by the factory's loopback/scheme/URL guard.");
    }

    private static bool IsGuardRejection(Exception ex)
    {
        return ex is InvalidOperationException
               && (ex.Message.Contains("loopback host", StringComparison.Ordinal)
                   || ex.Message.Contains("http or https scheme", StringComparison.Ordinal)
                   || ex.Message.Contains("absolute URL", StringComparison.Ordinal));
    }

    private static McpClientFactory CreateFactory()
    {
        var options = Options.Create(new McpOptions
        {
            ConnectTimeoutSeconds = 30,
            HttpLoopbackHosts = ["127.0.0.1", "localhost", "::1"]
        });
        return new McpClientFactory(options, NullLoggerFactory.Instance);
    }

    private static McpServerRecord HttpRecord(string url)
    {
        return new McpServerRecord(Guid.NewGuid(),
            "Remote",
            Description: null,
            McpTransportKind.Http,
            Command: null,
            Arguments: [],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            Url: url,
            Enabled: true,
            Version: 1,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0);
    }
}
