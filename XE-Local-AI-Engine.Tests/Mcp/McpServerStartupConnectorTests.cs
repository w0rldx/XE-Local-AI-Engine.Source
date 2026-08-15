namespace XE_Local_AI_Engine.Tests.Mcp;

using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for the startup MCP connect. The connector exists to move the first
///     <see cref="IMcpServerConnectionManager.RefreshAsync" /> off the hot path, and its contract is that a node still
///     starts when no MCP server can be reached — so the refresh-wide failure classes are swallowed and logged while
///     anything unexpected is left to escape rather than being silently absorbed.
/// </summary>
public sealed class McpServerStartupConnectorTests
{
    [Test]
    public async Task StartAsync_RefreshesTheEnabledServersOnce()
    {
        var manager = Substitute.For<IMcpServerConnectionManager>();
        var logger = new RecordingLogger<McpServerStartupConnector>();
        var connector = new McpServerStartupConnector(manager, logger);

        await connector.StartAsync(CancellationToken.None);

        await manager.Received(1).RefreshAsync(Arg.Any<CancellationToken>());
        AssertEx.Empty(logger.Entries);
    }

    [Test]
    [Arguments(typeof(InvalidOperationException))]
    [Arguments(typeof(IOException))]
    [Arguments(typeof(TimeoutException))]
    public async Task StartAsync_WhenTheRefreshFails_LogsAndLetsTheNodeStart(Type exceptionType)
    {
        var manager = Substitute.For<IMcpServerConnectionManager>();
        _ = manager.RefreshAsync(Arg.Any<CancellationToken>())
                   .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType)!);
        var logger = new RecordingLogger<McpServerStartupConnector>();
        var connector = new McpServerStartupConnector(manager, logger);

        await connector.StartAsync(CancellationToken.None);

        AssertEx.True(logger.HasEntry(LogLevel.Warning, "Initial MCP server connection refresh failed at startup"),
            "A best-effort connect that fails must still be reported.");
    }

    [Test]
    public async Task StartAsync_WhenCancelledDuringStartup_StopsQuietly()
    {
        var manager = Substitute.For<IMcpServerConnectionManager>();
        _ = manager.RefreshAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());
        var logger = new RecordingLogger<McpServerStartupConnector>();
        var connector = new McpServerStartupConnector(manager, logger);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await connector.StartAsync(cancellation.Token);

        AssertEx.Empty(logger.Entries);
    }

    [Test]
    public async Task StartAsync_WhenTheRefreshFailsUnexpectedly_Propagates()
    {
        // The swallow list is deliberately narrow: an unexpected failure class is a bug, not a disconnected server, and
        // must not be absorbed into a warning that nobody reads.
        var manager = Substitute.For<IMcpServerConnectionManager>();
        _ = manager.RefreshAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new NotSupportedException("bug"));
        var connector = new McpServerStartupConnector(manager, new RecordingLogger<McpServerStartupConnector>());

        _ = await AssertEx.ThrowsAsync<NotSupportedException>(() => connector.StartAsync(CancellationToken.None));
    }

    [Test]
    public async Task StopAsync_IsANoOp_BecauseTheManagerOwnsClientDisposal()
    {
        var manager = Substitute.For<IMcpServerConnectionManager>();
        var connector = new McpServerStartupConnector(manager, new RecordingLogger<McpServerStartupConnector>());

        await connector.StopAsync(CancellationToken.None);

        await manager.DidNotReceiveWithAnyArgs().RefreshAsync(default);
    }
}
