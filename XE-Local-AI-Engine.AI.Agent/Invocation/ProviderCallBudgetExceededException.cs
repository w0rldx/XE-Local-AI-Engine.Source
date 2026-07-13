namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     Raised at the raw provider boundary when one invocation's cumulative provider-round count or total estimated
///     input-token spend exceeds the configured <see cref="Configuration.ProviderCallBudgetOptions" /> ceilings — a
///     runaway autonomous loop (single-agent tool loop, approval resumes, or orchestration participant turns that never
///     converge). Thrown BEFORE the offending provider call, so the invocation fails cleanly with a classified, sanitized
///     terminal message instead of looping until an outer timeout or accumulating unbounded cost. The message is a fixed,
///     path-free constant carrying no token counts, model names, or content.
/// </summary>
public sealed class ProviderCallBudgetExceededException : InvalidOperationException
{
    public ProviderCallBudgetExceededException(string message)
        : base(message)
    {
    }
}
