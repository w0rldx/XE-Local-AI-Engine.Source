namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

/// <summary>
///     Raised when the conversation history still exceeds the resolved context budget after the deterministic
///     budgeter's truncation passes, leaving <see cref="ConversationBudgetResult.ExceedsBudget" /> true.
/// </summary>
/// <remarks>
///     Raised BEFORE any provider call for the affected round, so a turn that cannot be bounded fails cleanly with a
///     classified, sanitized message instead of silently overrunning the model's launched context window
///     (llama-server's <c>-c</c>) or being rejected deep inside the provider with an opaque error. The message is a
///     fixed, path-free constant (<c>InvocationRunner.ContextBudgetExceededMessage</c>) carrying no token counts,
///     model names or content.
/// </remarks>
public sealed class ContextBudgetExceededException : InvalidOperationException
{
    public ContextBudgetExceededException(string message)
        : base(message)
    {
    }
}
