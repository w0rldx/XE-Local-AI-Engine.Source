namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Common.Telemetry;

/// <summary>
///     A <see cref="DelegatingAIFunction" /> that bounds a single model-invoked MCP tool call with a per-call
///     deadline, wrapping the innermost MCP executable below the argument-repair and result-budget wrappers.
/// </summary>
/// <remarks>
///     The MCP SDK's <c>McpClientTool</c> carries no per-call timeout, so without this a wedged server's call is
///     bounded only emergently by the stream watchdog and invocation timeout, stalling the whole turn. On OUR timeout
///     — the linked token fired, the caller's did not — the call becomes a typed tool-failure <em>result</em>, never a
///     throw and NEVER a retry, since a tool call is non-idempotent; a genuine caller cancellation propagates
///     unchanged. It is transparent to name, description and schema, so it composes without changing the offer.
/// </remarks>
internal sealed class McpToolCallTimeoutAIFunction : DelegatingAIFunction
{
    private readonly TimeSpan _timeout;

    public McpToolCallTimeoutAIFunction(AIFunction innerFunction, TimeSpan timeout)
        : base(innerFunction)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _timeout = timeout;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            return await base.InvokeCoreAsync(arguments, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Only OUR deadline fired, not the run's cancellation: report a clean, model-actionable tool error and let the loop
            // continue. Never rethrow as cancel, which would surface as a run cancellation, and never retry a non-idempotent call.
            NodeMetrics.McpToolTimeoutTotal.Add(1);
            return
                $"The MCP tool '{Name}' did not respond within the configured {_timeout.TotalSeconds:0.##}s tool-call timeout and was cancelled. The server may be slow or unresponsive; do not retry the same call — continue without it or try a different approach.";
        }
    }
}
