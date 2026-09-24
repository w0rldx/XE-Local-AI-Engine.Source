namespace XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>
///     A tool approval nobody answered within the pending-tool-call age, raised both by the waiter's own timeout and by
///     the stale-call sweep so the turn fails with the same sentence whichever fired first.
/// </summary>
/// <remarks>
///     Derives from <see cref="TimeoutException" /> so the failure stays in the Timeout category; the classifier matches
///     it before the generic arm and surfaces the message verbatim, which is safe because it carries only a tool name.
/// </remarks>
public sealed class ApprovalExpiredException : TimeoutException
{
    public ApprovalExpiredException(string? toolName)
        : base(string.IsNullOrEmpty(toolName)
            ? "The approval for a tool call expired before anyone answered it."
            : $"The approval for tool '{toolName}' expired before anyone answered it.")
    {
    }
}
