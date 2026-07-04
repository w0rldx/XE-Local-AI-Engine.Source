namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using Microsoft.Extensions.AI;

/// <summary>
///     A <see cref="DelegatingAIFunction" /> backstop that bounds the textual size of a tool result before it enters
///     (and is re-sent on every subsequent turn of) the chat history. It wraps any executable tool — ClientLocal
///     handlers and MCP tools alike — and truncates an over-budget result via <see cref="ToolResultBudget" /> with an
///     explicit marker so the model can tell the output was clipped. Smaller per-tool caps that run inside the handler
///     still apply first; this is only the shared ceiling for the pathological case they miss. The wrapper is transparent
///     to name/description/schema (delegated to the inner function), so it composes underneath the approval wrapper
///     without changing what the model is offered.
/// </summary>
internal sealed class BudgetedToolResultAIFunction : DelegatingAIFunction
{
    private readonly int _maxResultCharacters;

    public BudgetedToolResultAIFunction(AIFunction innerFunction, int maxResultCharacters)
        : base(innerFunction)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResultCharacters);
        _maxResultCharacters = maxResultCharacters;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        return ToolResultBudget.Apply(result, _maxResultCharacters);
    }
}
