namespace XE_Local_AI_Engine.Tests.Mcp;

using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for the startup MCP connect. The connector moves the first
///     <see cref="IMcpServerConnectionManager.RefreshAsync(CancellationToken)" /> off the host start path, and its contract is that a
///     node still starts when no MCP server can be reached or answers: host start never waits on the refresh, and any refresh
///     failure is logged rather than stopping the node.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class McpServerStartupConnectorTests
{
    [Test]
    public async Task StartAsync_RefreshesTheEnabledServersOnce()
    {
        var manager = Substitute.For<IMcpServerConnectionManager>();
        var logger = new RecordingLogger<McpServerStartupConnector>();
        using var connector = new McpServerStartupConnector(manager, logger);

        await connector.StartAsync(CancellationToken.None);
        await connector.ExecuteTask!;

        await manager.Received(1).RefreshAsync(Arg.Any<CancellationToken>());
        AssertEx.Empty(logger.Entries);
    }

    [Test]
    [Timeout(60_000)]
    public async Task StartAsync_WhenRefreshNeverCompletes_ReturnsPromptly(CancellationToken cancellationToken)
    {
        // A server that accepts but never answers initialize must not hold host start (and with it the UI) for the
        // connect timeout.
        var manager = Substitute.For<IMcpServerConnectionManager>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = manager.RefreshAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return never.Task;
        });
        using var connector = new McpServerStartupConnector(manager, new RecordingLogger<McpServerStartupConnector>());

        try
        {
            var start = connector.StartAsync(cancellationToken);
            await entered.Task.WaitAsync(cancellationToken);

            AssertEx.True(start.IsCompleted, "Host start must not wait on the MCP refresh, which is running and has not finished.");
            AssertEx.False(never.Task.IsCompleted, "the refresh is still outstanding when start has returned");
            await start;
        }
        finally
        {
            never.TrySetResult();
            await connector.StopAsync(cancellationToken);
        }
    }

    [Test]
    [Arguments(typeof(InvalidOperationException))]
    [Arguments(typeof(IOException))]
    [Arguments(typeof(TimeoutException))]
    [Arguments(typeof(NotSupportedException))]
    public async Task StartAsync_WhenTheRefreshFails_LogsAndLetsTheNodeStart(Type exceptionType)
    {
        var manager = Substitute.For<IMcpServerConnectionManager>();
        _ = manager.RefreshAsync(Arg.Any<CancellationToken>())
                   .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType)!);
        var logger = new RecordingLogger<McpServerStartupConnector>();
        using var connector = new McpServerStartupConnector(manager, logger);

        await connector.StartAsync(CancellationToken.None);
        await connector.ExecuteTask!;

        AssertEx.True(logger.HasEntry(LogLevel.Warning, "Initial MCP server connection refresh failed at startup"),
            "A best-effort connect that fails must still be reported.");
    }

    [Test]
    public async Task StopAsync_WhileTheRefreshRuns_CancelsItQuietly()
    {
        var manager = Substitute.For<IMcpServerConnectionManager>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = manager.RefreshAsync(Arg.Any<CancellationToken>())
                   .Returns(call =>
                   {
                       entered.TrySetResult();
                       return Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                   });
        var logger = new RecordingLogger<McpServerStartupConnector>();
        using var connector = new McpServerStartupConnector(manager, logger);
        await connector.StartAsync(CancellationToken.None);
        await entered.Task;

        await connector.StopAsync(CancellationToken.None);

        AssertEx.True(connector.ExecuteTask!.IsCompletedSuccessfully, "Stop must end the refresh without a fault.");
        AssertEx.Empty(logger.Entries);
    }
}
