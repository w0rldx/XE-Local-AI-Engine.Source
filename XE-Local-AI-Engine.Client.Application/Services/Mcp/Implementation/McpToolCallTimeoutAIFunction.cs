namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Common.Telemetry;

/// <summary>
///     A <see cref="DelegatingAIFunction" /> that bounds a single model-invoked MCP tool call with a per-call deadline
///     (AUD4-18). The MCP SDK's <c>McpClientTool</c> carries no per-call timeout of its own, so without this a slow or
///     wedged server's tool call is bounded only emergently by the stream watchdog / invocation timeout, stalling the
///     whole turn. This wraps the innermost MCP executable (below the argument-repair and result-budget wrappers) and
///     runs the server round-trip under a linked <see cref="CancellationTokenSource.CancelAfter(System.TimeSpan)" />.
///     <para>
///         On OUR timeout — the linked token fired but the caller's token did not — the call is converted to a typed
///         tool-failure <em>result</em> (a returned string, not a throw) so the function-invocation loop sees a clean
///         tool error, surfaces it to the model, and the run continues. It is NEVER retried: a tool call is
///         non-idempotent. A genuine caller cancellation (the run itself was cancelled) propagates unchanged. The wrapper
///         is transparent to name/description/schema (delegated to the inner function), so it composes without changing
///         what the model is offered.
///     </para>
/// </summary>
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
            return await base.InvokeCoreAsync(arguments, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Only OUR deadline fired (not the run's cancellation): report a clean, model-actionable tool error and let
            // the loop continue. Never rethrow-as-cancel here — that would surface as a run cancellation, and never
            // retry — the tool call is non-idempotent.
            NodeMetrics.McpToolTimeoutTotal.Add(1);
            return $"The MCP tool '{Name}' did not respond within the configured {_timeout.TotalSeconds:0.##}s tool-call timeout and was cancelled. The server may be slow or unresponsive; do not retry the same call — continue without it or try a different approach.";
        }
    }
}
