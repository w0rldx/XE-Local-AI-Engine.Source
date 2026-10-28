namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Classifies MCP connect, list and call failures for <see cref="McpServerConnectionManager" /> and words them for the
///     UI: one fixed message per reason, plus the operator-facing text that is shown only after the registration's own
///     secret values are scrubbed from it.
/// </summary>
internal static class McpFailureDescriber
{
    // Stderr can hold anything the server printed, so only a short, secret-scrubbed tail reaches the UI.
    private const int MaxStderrTailCharacters = 1024;

    internal readonly record struct SessionFailure(McpConnectionFailureReason Reason, string Detail);

    internal static bool IsTransportFault(Exception exception)
    {
        // A protocol error (unknown tool, invalid params) is the server answering; everything else here means the session is gone.
        return exception is IOException or HttpRequestException or SocketException or ObjectDisposedException
               || (exception is McpException && exception is not McpProtocolException);
    }

    internal static SessionFailure DescribeCompletion(ClientCompletionDetails details, McpServerRecord? record)
    {
        return details switch
        {
            StdioClientCompletionDetails stdio => new SessionFailure(McpConnectionFailureReason.ServerExited,
                AppendStderrTail(string.Create(CultureInfo.InvariantCulture,
                        $"{SafeMessage(McpConnectionFailureReason.ServerExited)} Exit code {stdio.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}."),
                    JoinTail(stdio.StandardErrorTail), record)),
            HttpClientCompletionDetails { HttpStatusCode: HttpStatusCode.NotFound } => new SessionFailure(McpConnectionFailureReason.SessionLost, SafeMessage(McpConnectionFailureReason.SessionLost)),
            HttpClientCompletionDetails => new SessionFailure(McpConnectionFailureReason.Transport, SafeMessage(McpConnectionFailureReason.Transport)),
            _ => new SessionFailure(McpConnectionFailureReason.ServerExited, SafeMessage(McpConnectionFailureReason.ServerExited))
        };
    }

    private static string? JoinTail(IEnumerable<string>? lines)
    {
        return lines is null ? null : string.Join('\n', lines);
    }

    /// <summary>
    ///     Adds a <c>stderr:</c> line with the capped tail (unless the message already quotes it), then scrubs the
    ///     registration's own secret values from the whole text before it reaches the UI or the model.
    /// </summary>
    internal static string AppendStderrTail(string message, string? tail, McpServerRecord? record)
    {
        var text = Scrub(message, record);
        if (!string.IsNullOrWhiteSpace(tail) && !message.Contains(tail.Trim(), StringComparison.Ordinal))
        {
            // Scrub the WHOLE tail before the display cut, or the cut could split a secret and leave a matching-free suffix.
            var trimmed = Scrub(tail.Trim(), record);
            if (trimmed.Length > MaxStderrTailCharacters)
            {
                trimmed = string.Concat("...", trimmed.AsSpan(trimmed.Length - MaxStderrTailCharacters));
            }

            text = $"{text}\nstderr: {trimmed}";
        }

        return text;
    }

    private static string Scrub(string text, McpServerRecord? record)
    {
        // Value-based with an 8-char floor, so "-y" never blanks output; secret values are also split into their long parts, arguments
        // are whole tokens only (splitting a script argument blanked harmless words and paths). PATH is not a secret, the hint quotes it.
        if (record is null)
        {
            return new SecretValueRedactor([]).Redact(text);
        }

        var secretValues = record.Environment.Where(static pair => !SecretValueRedactor.IsSearchPathKey(pair.Key))
                                 .Select(static pair => pair.Value)
                                 .Concat(record.Headers.Values);
        return new SecretValueRedactor(SecretValueRedactor.ForCommand(secretValues, record.Arguments, minLength: 8)).Redact(text);
    }

    /// <summary>
    ///     The status text for a failed connect: fixed wording per reason, except the two whose exception message is
    ///     written for the operator (the Sandboxed PATH hint, the startup failure) and so is shown, scrubbed.
    /// </summary>
    internal static string DescribeFailure(Exception exception, McpConnectionFailureReason reason, McpServerRecord record)
    {
        return exception switch
        {
            FileNotFoundException notFound => Scrub(notFound.Message, record),
            McpServerStartupException startup => AppendStderrTail(startup.Message, startup.StderrTail, record),
            ClientTransportClosedException { Details: StdioClientCompletionDetails stdio } => AppendStderrTail(SafeMessage(reason), JoinTail(stdio.StandardErrorTail), record),
            _ => SafeMessage(reason)
        };
    }

    /// <summary>
    ///     Maps a caught connect, list or call failure to the reason the management panel words.
    /// </summary>
    /// <remarks>
    ///     A missing command is a <see cref="Win32Exception" /> wrapped in an <see cref="IOException" /> (SDK launch) or a
    ///     <see cref="FileNotFoundException" /> (Sandboxed pre-check); a process that died before the handshake is
    ///     <see cref="McpConnectionFailureReason.ServerStartupFailed" />. Order matters: all are <see cref="IOException" />s.
    ///     A 401 means "no credential" when the registration sends none and "wrong credential" when it does.
    /// </remarks>
    internal static McpConnectionFailureReason Classify(Exception exception, bool hasHeaders, bool inSession = false)
    {
        return exception switch
        {
            FileNotFoundException or IOException { InnerException: Win32Exception } => McpConnectionFailureReason.ServerNotFound,
            McpServerStartupException or ClientTransportClosedException { Details: StdioClientCompletionDetails } => McpConnectionFailureReason.ServerStartupFailed,
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => hasHeaders ? McpConnectionFailureReason.Authentication : McpConnectionFailureReason.AuthenticationRequired,
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => McpConnectionFailureReason.Forbidden,
            // Mid-session the server no longer knows the session: SessionLost, so the call's one retry does not hinge on Completion
            // having settled. At connect time a 404 is a wrong endpoint and stays Transport.
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } when inSession => McpConnectionFailureReason.SessionLost,
            AuthenticationException or HttpRequestException { InnerException: AuthenticationException } => McpConnectionFailureReason.Tls,
            TimeoutException => McpConnectionFailureReason.Timeout,
            HttpRequestException or IOException or SocketException => McpConnectionFailureReason.Transport,
            McpException or JsonException => McpConnectionFailureReason.Protocol,
            _ => McpConnectionFailureReason.Unknown
        };
    }

    internal static string SafeMessage(McpConnectionFailureReason reason)
    {
        // Connection, transport and sandbox messages can echo a command path, URL or protected host path, so the exception text
        // never reaches the UI: each reason has one fixed wording, and the real exception is logged server-side.
        return reason switch
        {
            McpConnectionFailureReason.SandboxUnavailable => OperatingSystem.IsWindows()
                ? "This node cannot isolate the MCP server from the host filesystem. On Windows that needs the MXC AppContainer boundary (Preview) available and execution previews enabled in Node settings, or move the server to the Privileged host tier."
                : "This node cannot isolate the MCP server from the host filesystem. Install bubblewrap (bwrap) with user-namespace support, or move the server to the Privileged host tier.",
            McpConnectionFailureReason.SandboxRefused =>
                "The sandbox refused to start the MCP server: its command or working directory is, or lies inside, a protected folder (a credential store, the engine's data or install folder), or the sandbox boundary could not be established. Point it at the folder holding the server's own files; the engine log names the path.",
            McpConnectionFailureReason.SandboxRefusedByProfile =>
                "The node's sandbox security profile is 'high', and this node's sandbox cannot impose the CPU, memory and process-count ceilings that profile requires for a Sandboxed MCP server. Install the missing mechanism the sandbox isolation summary names, or "
                + SandboxSecurityProfilePolicy.Remedy + ".",
            McpConnectionFailureReason.ServerNotFound => "The MCP server's command was not found or could not be started.",
            McpConnectionFailureReason.Authentication => "The MCP server rejected the configured credentials.",
            McpConnectionFailureReason.AuthenticationRequired => "The MCP server requires authentication, and no credential is configured. Add an Authorization header to the registration.",
            McpConnectionFailureReason.Forbidden => "The MCP server refused access with the configured credentials.",
            McpConnectionFailureReason.Tls => "The TLS negotiation with the MCP server failed.",
            McpConnectionFailureReason.Timeout => "Timed out connecting to the MCP server.",
            McpConnectionFailureReason.Transport => "The connection to the MCP server failed or closed.",
            McpConnectionFailureReason.Protocol => "The MCP server did not complete the MCP handshake.",
            McpConnectionFailureReason.ServerExited => "The MCP server process exited.",
            McpConnectionFailureReason.SessionLost => "The MCP server no longer recognises this node's session; it has probably restarted.",
            McpConnectionFailureReason.ServerStartupFailed => "The MCP server exited before completing the MCP handshake.",
            _ => "The MCP server connection failed."
        };
    }
}
