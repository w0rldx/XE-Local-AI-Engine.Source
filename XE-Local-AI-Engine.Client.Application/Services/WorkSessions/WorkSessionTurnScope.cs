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
    private static readonly AsyncLocal<WorkSessionOutputSchema?> AmbientOutputSchema = new();
    private static readonly AsyncLocal<IReadOnlyList<WorkSessionReadableArtifact>?> AmbientReadableArtifacts = new();

    /// <summary>Whether the current async flow is a work-session step.</summary>
    public static bool IsActive => AmbientIsWorkSessionTurn.Value;

    /// <summary>The output contract <c>complete_work_session</c> holds this step to, or null when its caller declared none.</summary>
    public static WorkSessionOutputSchema? OutputSchema => AmbientOutputSchema.Value;

    /// <summary>The upstream artifacts <c>read_upstream_artifact</c> may read in this step; empty outside a step that was handed any.</summary>
    public static IReadOnlyList<WorkSessionReadableArtifact> ReadableArtifacts => AmbientReadableArtifacts.Value ?? [];

    /// <summary>Seeds the flag, the step's output contract and its readable artifacts for the current async flow; disposal restores the prior values.</summary>
    public static IDisposable Begin(WorkSessionOutputSchema? outputSchema = null, IReadOnlyList<WorkSessionReadableArtifact>? readableArtifacts = null)
    {
        var previous = new Scope(AmbientIsWorkSessionTurn.Value, AmbientOutputSchema.Value, AmbientReadableArtifacts.Value);
        AmbientIsWorkSessionTurn.Value = true;
        AmbientOutputSchema.Value = outputSchema;
        AmbientReadableArtifacts.Value = readableArtifacts;
        return previous;
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

        // The read tool has nothing to read outside a step that was handed artifacts, so an agent listing it by name is not offered it there.
        var reads = IsActive && ReadableArtifacts.Count > 0;
        if (!reads && projected.Any(static tool => string.Equals(tool.Name, WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName, StringComparison.Ordinal)))
        {
            projected = [.. projected.Where(static tool => !string.Equals(tool.Name, WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName, StringComparison.Ordinal))];
        }

        if (!IsActive)
        {
            return projected;
        }

        var missing = pool.Where(tool => (WorkSessionToolDefinitions.ToolNames.Contains(tool.Name, StringComparer.Ordinal)
                                          || (reads && string.Equals(tool.Name, WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName, StringComparison.Ordinal)))
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
        private readonly WorkSessionOutputSchema? _previousOutputSchema;
        private readonly IReadOnlyList<WorkSessionReadableArtifact>? _previousReadableArtifacts;

        public Scope(bool previous, WorkSessionOutputSchema? previousOutputSchema, IReadOnlyList<WorkSessionReadableArtifact>? previousReadableArtifacts)
        {
            _previous = previous;
            _previousOutputSchema = previousOutputSchema;
            _previousReadableArtifacts = previousReadableArtifacts;
        }

        public void Dispose()
        {
            AmbientIsWorkSessionTurn.Value = _previous;
            AmbientOutputSchema.Value = _previousOutputSchema;
            AmbientReadableArtifacts.Value = _previousReadableArtifacts;
        }
    }
}
