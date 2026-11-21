namespace XE_Local_AI_Engine.Tests.Mcp;

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Tests.Testing;
// System.ComponentModel declares its own CategoryAttribute, and a file-scoped using beats the global one.
using CategoryAttribute = CategoryAttribute;

/// <summary>
///     Verifies the in-process MCP server harness every connection-manager test relies on: a real client can list and call
///     its tools, and its teardown is bounded even with a handler that never returns.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class InProcMcpServerSmokeTests
{
    private static readonly TimeSpan SignalWait = TimeSpan.FromSeconds(30);

    /// <summary>Fails a passing test whose in-process server teardown hit its deadline without the test consuming that expiry.</summary>
    [After(Test)]
    public void FailOnUnconsumedTeardownTimeout(TestContext context)
    {
        McpTeardownLog.ThrowIfUnconsumed(context, context.Execution.Result?.State == TestState.Failed);
    }

    [Test]
    public async Task InProcServer_ListsAndCallsExposedTools()
    {
        await using var server = await InProcMcpServer.StartAsync("test-server",
            AIFunctionFactory.Create(Echo));

        var tools = await server.Client.ListToolsAsync();

        AssertEx.Contains(tools.Select(static tool => tool.Name), "Echo");
    }

    [Test]
    public async Task Dispose_WithAnUncancellableToolCallInFlight_CompletesWithinTheBound()
    {
        // McpServer.DisposeAsync waits for in-flight handlers, so a handler that ignores its token used to hang the test host.
        var bound = TimeSpan.FromSeconds(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = await InProcMcpServer.StartAsync("wedged", bound, AIFunctionFactory.Create(async () =>
        {
            entered.TrySetResult();
            await gate.Task;
            return "released";
        }, "hold"));
        var call = Task.CompletedTask;
        Task? pending = null;
        try
        {
            call = server.Client.CallToolAsync("hold").AsTask();
            await entered.Task.WaitAsync(SignalWait);

            var stopwatch = Stopwatch.StartNew();
            await server.DisposeAsync();
            stopwatch.Stop();

            AssertEx.True(stopwatch.Elapsed < bound + TimeSpan.FromSeconds(9), $"teardown took {stopwatch.Elapsed}, past its {bound} bound.");
            AssertEx.True(McpTeardownLog.TryConsume(McpTeardownLog.CurrentScope), "the expired teardown must be recorded.");
            pending = AssertEx.NotNull(server.PendingTeardown, "the unfinished teardown must stay observable.");
        }
        finally
        {
            gate.TrySetResult();
            // The client call's own outcome is not under test, so WhenAny observes it without rethrowing.
            await Task.WhenAny(call.WaitAsync(SignalWait));
            await server.DisposeAsync();
        }

        // Releasing the handler lets the real teardown finish: the wedged handler ended rather than leaked.
        await AssertEx.NotNull(pending).WaitAsync(SignalWait);
    }

    [Test]
    public async Task Dispose_AfterAStopThatTimedOut_StillDisposesTheClient()
    {
        var bound = TimeSpan.FromSeconds(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = await InProcMcpServer.StartAsync("wedged-stop", bound, AIFunctionFactory.Create(async () =>
        {
            entered.TrySetResult();
            await gate.Task;
            return "released";
        }, "hold"));
        var call = Task.CompletedTask;
        Task? pending = null;
        try
        {
            call = server.Client.CallToolAsync("hold").AsTask();
            await entered.Task.WaitAsync(SignalWait);
            await server.StopServerAsync();
            AssertEx.True(McpTeardownLog.TryConsume(McpTeardownLog.CurrentScope), "the expired stop must be recorded.");
            var stop = AssertEx.NotNull(server.PendingTeardown, "the unfinished stop must stay observable.");

            // The stop is still wedged in the server's dispose; the client's own dispose must not wait behind it.
            await server.DisposeAsync();

            await AssertEx.CompletesAsync(Task.WhenAny(server.Client.Completion), TestBudgets.Contended, "the client must be disposed although the stop timed out.");
            pending = AssertEx.NotNull(server.PendingTeardown, "the earlier stop must stay observable after the dispose.");
            AssertEx.False(stop.IsCompleted, "the stop is still wedged on the gated handler.");
        }
        finally
        {
            gate.TrySetResult();
            // The client call's own outcome is not under test, so WhenAny observes it without rethrowing.
            await Task.WhenAny(call.WaitAsync(SignalWait));
            await server.DisposeAsync();
        }

        await AssertEx.NotNull(pending).WaitAsync(SignalWait);
    }

    [Test]
    public async Task Dispose_WithACancellableToolCallInFlight_RecordsNoTimeout()
    {
        // Pins that stopping the server cancels a handler's token, so a handler that honours it never reaches the deadline.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = await InProcMcpServer.StartAsync("cancellable", AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
            return "released";
        }, "hold"));
        var call = Task.CompletedTask;
        try
        {
            call = server.Client.CallToolAsync("hold").AsTask();
            await entered.Task.WaitAsync(SignalWait);

            await server.DisposeAsync();

            AssertEx.False(McpTeardownLog.TryConsume(McpTeardownLog.CurrentScope), "a handler that honours its token must not hold teardown past the bound.");
            AssertEx.Null(server.PendingTeardown);
        }
        finally
        {
            gate.TrySetResult();
            // The client call's own outcome is not under test, so WhenAny observes it without rethrowing.
            await Task.WhenAny(call.WaitAsync(SignalWait));
            await server.DisposeAsync();
        }
    }

    [Test]
    public void TeardownTimeoutAfterAPassingBody_FailsTheTest()
    {
        // The After(Test) hook runs exactly this check for the test's own scope; a fresh scope keeps the probe out of this test's entry.
        var scope = new object();
        McpTeardownLog.Record(scope, "probe-server", "server dispose");

        var failure = AssertEx.Throws<AssertionException>(() => McpTeardownLog.ThrowIfUnconsumed(scope, bodyFailed: false));

        AssertEx.Contains(failure.Message, "probe-server (stuck at server dispose)");
        McpTeardownLog.Record(scope, "consumed-server", "server loop");
        AssertEx.True(McpTeardownLog.TryConsume(scope));
        McpTeardownLog.ThrowIfUnconsumed(scope, bodyFailed: false);
        McpTeardownLog.Record(scope, "failed-body-server", "server loop");
        McpTeardownLog.ThrowIfUnconsumed(scope, bodyFailed: true);
        AssertEx.False(McpTeardownLog.TryConsume(scope), "the hook forgets the scope either way.");
    }

    [Description("Echoes the input back.")]
    private static string Echo([Description("The value to echo.")] string value)
    {
        return value;
    }
}
