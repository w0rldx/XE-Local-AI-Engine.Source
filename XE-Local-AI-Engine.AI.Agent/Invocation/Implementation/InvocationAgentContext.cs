namespace XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
///     Runtime objects required to execute and dispose one single-agent invocation.
/// </summary>
/// <remarks>
///     The context deliberately carries Microsoft Agent Framework types only inside the agent assembly boundary.
///     Application-layer callers consume the normalized seed messages, options, and metadata without constructing
///     agents themselves.
/// </remarks>
public sealed class InvocationAgentContext : IAsyncDisposable
{
    public required AIAgent Agent { get; init; }

    public AgentSession? Session { get; init; }

    public required IReadOnlyList<ChatMessage> SeedMessages { get; init; }

    public AgentRunOptions? RunOptions { get; set; }

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        if (Session is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }
}
