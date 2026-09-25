namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;

/// <summary>
///     Marks the current async flow as a work-session step, so the agent resolver offers the four state tools to
///     WHATEVER agent drives the session, flowed as an <see cref="AsyncLocal{T}" /> like <c>ToolResultBudgetScope</c>.
/// </summary>
/// <remarks>
///     The state tools are how a session ends (<c>complete_work_session</c>) and how it records anything. Reaching an
///     agent only through its <c>AllowedToolNames</c> meant a custom agent, or one a development-workflow node was bound
///     to, ran every step tool-less until the step cap. They are a property of the TURN, not of the agent, so the
///     supervisor seeds this around the send and <c>AgentDefinitionResolver</c> unions them in after the intersection. An
///     ordinary chat turn never carries the scope and is never offered them.
/// </remarks>
internal static class WorkSessionTurnScope
{
    private static readonly AsyncLocal<bool> AmbientIsWorkSessionTurn = new();

    /// <summary>Whether the current async flow is a work-session step.</summary>
    public static bool IsActive => AmbientIsWorkSessionTurn.Value;

    /// <summary>Seeds the flag for the current async flow; disposal restores the prior value.</summary>
    public static IDisposable Begin()
    {
        var previous = AmbientIsWorkSessionTurn.Value;
        AmbientIsWorkSessionTurn.Value = true;
        return new Scope(previous);
    }

    /// <summary>
    ///     Returns <paramref name="projected" /> with every state tool the <paramref name="pool" /> carries added, each
    ///     approval flag composed by the node policy; unchanged outside a work-session step.
    /// </summary>
    /// <remarks>
    ///     Lifted from the pool, never fabricated, so the capability and allow-list gates the pool already applied still
    ///     decide whether a model gets them at all.
    /// </remarks>
    public static IReadOnlyList<AllowedToolDto> EnsureStateTools(IReadOnlyList<AllowedToolDto> projected,
        IReadOnlyList<AllowedToolDto> pool,
        IToolApprovalPolicy toolApprovalPolicy)
    {
        ArgumentNullException.ThrowIfNull(projected);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(toolApprovalPolicy);

        if (!IsActive)
        {
            return projected;
        }

        var missing = pool.Where(tool => WorkSessionToolDefinitions.ToolNames.Contains(tool.Name, StringComparer.Ordinal)
                                         && !projected.Any(existing => string.Equals(existing.Name, tool.Name, StringComparison.Ordinal)))
                          .Select(tool => tool with
                          {
                              RequiresApproval = toolApprovalPolicy.RequiresApproval(tool.Name, tool.Category, tool.RequiresApproval)
                          })
                          .ToArray();
        return missing.Length == 0 ? projected : [.. projected, .. missing];
    }

    private sealed class Scope : IDisposable
    {
        private readonly bool _previous;

        public Scope(bool previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            AmbientIsWorkSessionTurn.Value = _previous;
        }
    }
}
