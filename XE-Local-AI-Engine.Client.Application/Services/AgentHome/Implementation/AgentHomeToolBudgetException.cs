namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     Thrown by the goal loop's tool gateway when the inner agent asks for more tool calls than
///     <see cref="AgentHomeOptions.MaxInnerToolCalls" /> allows.
/// </summary>
/// <remarks>
///     It ends the loop rather than returning a refusal the model would keep spending turns against. The executor
///     catches it and reports a budget-capped run whose partial work still exports.
/// </remarks>
internal sealed class AgentHomeToolBudgetException : InvalidOperationException
{
    public AgentHomeToolBudgetException(string message)
        : base(message)
    {
    }

    public AgentHomeToolBudgetException()
    {
    }

    public AgentHomeToolBudgetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
