namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using Microsoft.Extensions.AI;

/// <summary>
///     A <see cref="DelegatingAIFunction" /> backstop that bounds the textual size of a tool result before it enters —
///     and is re-sent on every later turn of — the chat history.
/// </summary>
/// <remarks>
///     Wraps any executable tool, ClientLocal handlers and MCP tools alike, and truncates an over-budget result via
///     <see cref="ToolResultBudget" /> with an explicit marker. Smaller per-tool caps inside the handler still apply
///     first; this is only the shared ceiling for the pathological case they miss. Transparent to name, description and
///     schema, so it composes underneath the approval wrapper without changing what the model is offered.
/// </remarks>
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

        // The configured budget is a node-wide constant; a run needing a tighter ceiling seeds ToolResultBudgetScope,
        // and the tighten-only resolve lives here because this wrapper is the choke point for every tool kind.
        return ToolResultBudget.Apply(result, ToolResultBudgetScope.Resolve(_maxResultCharacters));
    }
}
