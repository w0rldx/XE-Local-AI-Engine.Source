namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

/// <summary>
///     Raised when the conversation history still exceeds the resolved context budget after the deterministic
///     budgeter's two-pass truncation (oversized historical tool results excerpted, then whole turns dropped) — i.e.
///     <see cref="ConversationBudgetResult.ExceedsBudget" /> stayed true. Raised BEFORE any provider call for the
///     affected round, so a turn that cannot be bounded fails cleanly with a classified, sanitized message instead of
///     silently overrunning the model's launched context window (llama-server's <c>-c</c>) or being rejected deep
///     inside the provider with an opaque error. The message is a fixed, path-free constant (see
///     <c>InvocationRunner.ContextBudgetExceededMessage</c>); it never carries token counts, model names, or content.
/// </summary>
public sealed class ContextBudgetExceededException : InvalidOperationException
{
    public ContextBudgetExceededException(string message)
        : base(message)
    {
    }
}
