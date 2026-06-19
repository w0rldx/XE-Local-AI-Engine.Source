namespace XE_Local_AI_Engine.Tests.Mcp;

using System.IO.Pipelines;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

/// <summary>
///     A fully in-process MCP server for tests: it wires a bidirectional in-memory stream pair between a real
///     <see cref="McpServer" /> and a real <see cref="McpClient" />, so the connection manager can be driven through
///     the genuine SDK protocol (connect, list-tools, call-tool) without spawning a stdio process or opening a socket.
///     The server exposes the supplied <see cref="AIFunction" />s as tools. Dispose tears down the client, the server
///     loop, and the streams.
/// </summary>
internal sealed class InProcMcpServer : IAsyncDisposable
{
    private readonly Stream _clientInput;
    private readonly Stream _clientOutput;
    private readonly McpServer _server;
    private readonly CancellationTokenSource _serverCts;
    private readonly Stream _serverInput;
    private readonly Task _serverLoop;
    private readonly Stream _serverOutput;
    private readonly StreamServerTransport _serverTransport;

    private InProcMcpServer(McpServer server,
        StreamServerTransport serverTransport,
        Task serverLoop,
        CancellationTokenSource serverCts,
        Stream serverInput,
        Stream serverOutput,
        Stream clientInput,
        Stream clientOutput,
        McpClient client)
    {
        _server = server;
        _serverTransport = serverTransport;
        _serverLoop = serverLoop;
        _serverCts = serverCts;
        _serverInput = serverInput;
        _serverOutput = serverOutput;
        _clientInput = clientInput;
        _clientOutput = clientOutput;
        Client = client;
    }

    /// <summary>The connected client bound to this server. Hand this to the test's fake client factory.</summary>
    public McpClient Client { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Client.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Ignore teardown races between client dispose and the server loop ending.
        }

        await _serverCts.CancelAsync();
        try
        {
            // Bound the wait: if the SDK's read loop does not observe cancellation promptly (e.g. it is parked on a
            // stream read whose peer has already closed), teardown must still complete rather than hang the test run.
            await _serverLoop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The loop ends when the transport closes; cancellation/timeout/IO during teardown is expected.
        }

        await _server.DisposeAsync();
        await _serverTransport.DisposeAsync();
        await _serverInput.DisposeAsync();
        await _serverOutput.DisposeAsync();
        await _clientInput.DisposeAsync();
        await _clientOutput.DisposeAsync();
        _serverCts.Dispose();
    }

    public static async Task<InProcMcpServer> StartAsync(string serverName, params AIFunction[] tools)
    {
        // Give every instance a unique diagnostic name so concurrent in-proc servers can never collide on a fixed
        // transport/server name across the parallel suite. This is independent of the connection manager's slug (which
        // derives from the McpServerRecord.Name), so the qualified tool names the tests assert are unaffected.
        var uniqueName = $"{serverName}-{Guid.NewGuid():N}";

        // Two pipes cross-wired: the server reads what the client writes and vice versa.
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var serverInput = clientToServer.Reader.AsStream();
        var serverOutput = serverToClient.Writer.AsStream();
        var clientInput = serverToClient.Reader.AsStream();
        var clientOutput = clientToServer.Writer.AsStream();

        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            toolCollection.Add(McpServerTool.Create(tool));
        }

        var serverOptions = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = uniqueName,
                Version = "1.0.0"
            },
            ToolCollection = toolCollection
        };

        var serverTransport = new StreamServerTransport(serverInput, serverOutput, uniqueName, NullLoggerFactory.Instance);
        var server = McpServer.Create(serverTransport, serverOptions, NullLoggerFactory.Instance, null);

        var serverCts = new CancellationTokenSource();
        var serverLoop = server.RunAsync(serverCts.Token);

        // Bound the client handshake: under heavy parallel load the SDK's stream pumping can be thread-pool-starved, so
        // a hard deadline turns a stall into a deterministic, fast failure instead of a hang or an intermittent race.
        using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // StreamClientTransport's first arg (serverInput) is the stream the client WRITES to reach the server, and the
        // second (serverOutput) is the stream the client READS the server's replies from (per the SDK ctor contract).
        var clientTransport = new StreamClientTransport(clientOutput, clientInput, NullLoggerFactory.Instance);
        var client = await McpClient.CreateAsync(clientTransport, null, NullLoggerFactory.Instance, handshakeCts.Token);

        return new InProcMcpServer(server, serverTransport, serverLoop, serverCts, serverInput, serverOutput, clientInput, clientOutput, client);
    }
}
