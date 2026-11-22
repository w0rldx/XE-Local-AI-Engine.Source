namespace XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>
///     Marks the current async flow as a run nobody waits on (a schedule, a workflow, an integration, a benchmark, a
///     work-session step or an AgentHome goal), flowed as an <see cref="AsyncLocal{T}" /> like <c>WorkSessionTurnScope</c>.
/// </summary>
/// <remarks>
///     The tool-relevance selector and the playbook ranker read it to embed as a <c>Background</c> load, so such a run
///     never reloads an embedder the operator ejected and degrades to lexical ranking instead. A chat turn and an inbound
///     MCP request never carry it.
/// </remarks>
internal static class UnattendedRunScope
{
    private static readonly AsyncLocal<bool> AmbientIsUnattended = new();

    /// <summary>Whether the current async flow is an unattended run.</summary>
    public static bool IsActive => AmbientIsUnattended.Value;

    /// <summary>Seeds the flag for the current async flow; disposal restores the prior value.</summary>
    public static IDisposable Begin()
    {
        var previous = new Scope(AmbientIsUnattended.Value);
        AmbientIsUnattended.Value = true;
        return previous;
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
            AmbientIsUnattended.Value = _previous;
        }
    }
}
