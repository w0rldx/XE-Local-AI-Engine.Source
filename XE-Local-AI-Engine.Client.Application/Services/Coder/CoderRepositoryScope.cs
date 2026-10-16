namespace XE_Local_AI_Engine.Client.Services.Coder;

/// <summary>
///     The registered repository the read-only coder tools serve in the current async flow, instead of the AgentHome
///     workspace, flowed as an <see cref="AsyncLocal{T}" /> like <c>WorkSessionTurnScope</c>.
/// </summary>
/// <remarks>
///     Seeded by the work-session supervisor around a step whose development-workflow node is bound to a Development project,
///     so a research node reads the repository its work item names. The root is the canonical host path the repository
///     binding resolved; the reads stay confined to it by the same guards a jail read uses.
/// </remarks>
internal static class CoderRepositoryScope
{
    private static readonly AsyncLocal<string?> AmbientRoot = new();

    /// <summary>The repository root the coder tools read in this flow, or null for the AgentHome workspace.</summary>
    public static string? Root => AmbientRoot.Value;

    /// <summary>Seeds the root for the current async flow, null meaning the AgentHome workspace; disposal restores the prior value.</summary>
    public static IDisposable Begin(string? root)
    {
        var previous = AmbientRoot.Value;
        AmbientRoot.Value = root;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;

        public Scope(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            AmbientRoot.Value = _previous;
        }
    }
}
