namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Each MCP tool call has a per-call timeout. On expiry the wrapper returns a typed tool-failure RESULT
///     (the run continues), never throws as a cancel or retries, and increments <c>mcp_tool_timeout_total</c>; a caller
///     cancellation propagates.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class McpToolCallTimeoutAIFunctionTests
{
    [Test]
    public async Task InvokeAsync_WhenInnerToolExceedsTimeout_ReturnsTypedFailureAndCountsMetric()
    {
        var invocations = 0;
        var inner = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            _ = Interlocked.Increment(ref invocations);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "unreachable";
        }, "slow_tool");

        var sut = new McpToolCallTimeoutAIFunction(inner, TimeSpan.FromMilliseconds(50));

        long observedTimeouts = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == NodeMetrics.MeterName && instrument.Name == "mcp_tool_timeout_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref observedTimeouts, measurement));
        listener.Start();

        var result = await sut.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        listener.Dispose();

        AssertEx.True(result?.ToString()?.Contains("did not respond within", StringComparison.Ordinal) == true,
            "Expected a typed tool-failure result naming the timeout.");
        AssertEx.Equal(ToolFailureText.TimeoutCode, ToolFailureText.TryGetCode(result), "The audit outcome must read timeout, not success.");
        AssertEx.Equal(expected: 1, invocations);
        AssertEx.Equal(expected: 1L, observedTimeouts);
    }

    [Test]
    public async Task InvokeAsync_WhenCallerCancels_PropagatesCancellationRatherThanReturningToolError()
    {
        var inner = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "unreachable";
        }, "slow_tool");

        // A generous per-call timeout so the CALLER's cancellation, not the wrapper's deadline, is what fires.
        var sut = new McpToolCallTimeoutAIFunction(inner, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => sut.InvokeAsync(new AIFunctionArguments(), cts.Token).AsTask());
    }

    [Test]
    public async Task InvokeAsync_WhenInnerCompletesInTime_ReturnsTheInnerResult()
    {
        var inner = AIFunctionFactory.Create((string value) => $"echo:{value}", "echo_tool");
        var sut = new McpToolCallTimeoutAIFunction(inner, TimeSpan.FromSeconds(30));

        var result = await sut.InvokeAsync(new AIFunctionArguments
        {
            ["value"] = "hi"
        }, CancellationToken.None);

        AssertEx.True(result?.ToString()?.Contains("echo:hi", StringComparison.Ordinal) == true, "Expected the inner tool result to pass through unchanged.");
    }
}
