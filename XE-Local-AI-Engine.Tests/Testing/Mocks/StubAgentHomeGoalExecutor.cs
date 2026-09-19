namespace XE_Local_AI_Engine.Tests.Testing.Mocks;

using XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     A recording <see cref="IAgentHomeGoalExecutor" /> for the lifecycle tests, which grade orchestration — the
///     lease, the workspace copy, the patch gate, cancel/owner hardening — and not the inner agent loop.
///     <para>
///         The loop ITSELF is graded by <c>AgentHomeGoalExecutorTests</c> against the real executor, the real coder
///         reader, the real process sandbox and a scripted chat client. This double exists so a lifecycle test does not
///         have to stand up a model to assert that a lease was released.
///     </para>
/// </summary>
internal sealed class StubAgentHomeGoalExecutor : IAgentHomeGoalExecutor
{
    private readonly Func<AgentHomeGoalRequest, CancellationToken, Task<AgentHomeGoalOutcome>> _behavior;

    public StubAgentHomeGoalExecutor(Func<AgentHomeGoalRequest, CancellationToken, Task<AgentHomeGoalOutcome>>? behavior = null)
    {
        _behavior = behavior ?? ((_, _) => Task.FromResult(new AgentHomeGoalOutcome
        {
            Status = AgentHomeGoalStatus.Completed
        }));
    }

    /// <summary>The requests this executor was handed, in order.</summary>
    public List<AgentHomeGoalRequest> Requests { get; } = [];

    public async Task<AgentHomeGoalOutcome> ExecuteAsync(AgentHomeGoalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Add(request);
        return await _behavior(request, cancellationToken);
    }
}
