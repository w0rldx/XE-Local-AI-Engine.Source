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
/// <remarks>
///     The whole teardown runs under one deadline, because <c>McpServer.DisposeAsync</c> waits for in-flight handlers. An expiry is
///     recorded in <see cref="McpTeardownLog" />, never thrown from <c>await using</c>, and the unfinished teardown stays in
///     <see cref="PendingTeardown" />.
/// </remarks>
internal sealed class InProcMcpServer : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTeardownBound = TimeSpan.FromSeconds(10);

    private readonly Stream _clientInput;
    private readonly Stream _clientOutput;
    private readonly string _name;
    private readonly McpServer _server;
    private readonly CancellationTokenSource _serverCts;
    private readonly Stream _serverInput;
    private readonly Task _serverLoop;
    private readonly Stream _serverOutput;
    private readonly StreamServerTransport _serverTransport;
    private readonly TimeSpan _teardownBound;
    private readonly McpServerPrimitiveCollection<McpServerTool> _tools;
    private bool _disposed;
    private bool _serverStopped;
    private volatile string _teardownStep = "not started";

    private InProcMcpServer(string name,
        TimeSpan teardownBound,
        McpServer server,
        StreamServerTransport serverTransport,
        Task serverLoop,
        CancellationTokenSource serverCts,
        Stream serverInput,
        Stream serverOutput,
        Stream clientInput,
        Stream clientOutput,
        McpClient client,
        McpServerPrimitiveCollection<McpServerTool> tools)
    {
        _name = name;
        _teardownBound = teardownBound;
        _tools = tools;
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

    /// <summary>A teardown that outlived its deadline, still running; a test awaits it to prove the wedged handler ended.</summary>
    public Task? PendingTeardown { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await RunBoundedAsync(DisposeCoreAsync);
    }

    private async Task DisposeCoreAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _teardownStep = "client dispose";
        try
        {
            await Client.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Ignore teardown races between client dispose and the server loop ending.
        }

        await StopServerCoreAsync();
        _teardownStep = "client streams";
        await _clientInput.DisposeAsync();
        await _clientOutput.DisposeAsync();
        _serverCts.Dispose();
    }

    private async Task RunBoundedAsync(Func<Task> teardown)
    {
        // Both teardown steps are idempotent, so a dispose after a timed-out stop still disposes the client and never waits for that stop.
        var run = teardown();
        try
        {
            await run.WaitAsync(_teardownBound, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // An earlier expiry stays observable alongside this one.
            PendingTeardown = PendingTeardown is { IsCompleted: false } prior ? Task.WhenAll(prior, run) : run;
            McpTeardownLog.Record(McpTeardownLog.CurrentScope, _name, _teardownStep);
        }
    }

    /// <summary>
    ///     Adds a tool to the running server; the SDK server announces the change with <c>notifications/tools/list_changed</c>.
    /// </summary>
    public void AddTool(AIFunction tool)
    {
        _tools.Add(McpServerTool.Create(tool));
    }

    /// <summary>
    ///     Kills the server side only, the in-process shape of a crashed server: the loop stops and its output stream
    ///     closes, so the still-undisposed <see cref="Client" /> sees end-of-stream and its <c>Completion</c> fires.
    /// </summary>
    public Task StopServerAsync()
    {
        return RunBoundedAsync(StopServerCoreAsync);
    }

    private async Task StopServerCoreAsync()
    {
        if (_serverStopped)
        {
            return;
        }

        _serverStopped = true;
        _teardownStep = "server cancel";
        await _serverCts.CancelAsync();
        _teardownStep = "server loop";
        try
        {
            // Bound the wait: if the SDK's read loop does not observe cancellation promptly (e.g. it is parked on a
            // stream read whose peer has already closed), teardown must still complete rather than hang the test run.
            await _serverLoop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The loop ends when the transport closes; cancellation/timeout/IO during teardown is expected.
        }

        _teardownStep = "server dispose";
        await _server.DisposeAsync();
        _teardownStep = "server transport";
        await _serverTransport.DisposeAsync();
        _teardownStep = "server streams";
        await _serverInput.DisposeAsync();
        await _serverOutput.DisposeAsync();
    }

    public static Task<InProcMcpServer> StartAsync(string serverName, params AIFunction[] tools)
    {
        return StartAsync(serverName, protocolVersion: null, tools);
    }

    /// <summary>Starts a server whose whole teardown is bounded by <paramref name="teardownBound" /> instead of the default 10 s.</summary>
    public static Task<InProcMcpServer> StartAsync(string serverName, TimeSpan teardownBound, params AIFunction[] tools)
    {
        return StartCoreAsync(serverName, protocolVersion: null, teardownBound, tools);
    }

    /// <summary>
    ///     Starts a server whose client pins <paramref name="protocolVersion" />, such as <c>2025-11-25</c>.
    /// </summary>
    /// <remarks>
    ///     The 2026-07-28 revision delivers <c>*/list_changed</c> only on a <c>subscriptions/listen</c> stream, so a test
    ///     of the unsolicited notification third-party servers send pins an initialize-era revision.
    /// </remarks>
    public static Task<InProcMcpServer> StartAsync(string serverName, string? protocolVersion, params AIFunction[] tools)
    {
        return StartCoreAsync(serverName, protocolVersion, DefaultTeardownBound, tools);
    }

    private static async Task<InProcMcpServer> StartCoreAsync(string serverName, string? protocolVersion, TimeSpan teardownBound, AIFunction[] tools)
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
        var server = McpServer.Create(serverTransport, serverOptions, NullLoggerFactory.Instance);

        var serverCts = new CancellationTokenSource();
        var serverLoop = server.RunAsync(serverCts.Token);

        // Bound the client handshake: under heavy parallel load the SDK's stream pumping can be thread-pool-starved, so
        // a hard deadline turns a stall into a deterministic, fast failure instead of a hang or an intermittent race.
        using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // StreamClientTransport's first arg (serverInput) is the stream the client WRITES to reach the server, and the
        // second (serverOutput) is the stream the client READS the server's replies from (per the SDK ctor contract).
        var clientTransport = new StreamClientTransport(clientOutput, clientInput, NullLoggerFactory.Instance);
        var clientOptions = protocolVersion is null
            ? null
            : new McpClientOptions
            {
                ProtocolVersion = protocolVersion
            };
        var client = await McpClient.CreateAsync(clientTransport, clientOptions, NullLoggerFactory.Instance, handshakeCts.Token);

        return new InProcMcpServer(uniqueName, teardownBound, server, serverTransport, serverLoop, serverCts, serverInput, serverOutput, clientInput, clientOutput, client, toolCollection);
    }
}
