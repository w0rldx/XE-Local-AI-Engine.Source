namespace XE_Local_AI_Engine.AI.Agent.Invocation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

public sealed class InvocationAgentContext : IAsyncDisposable
{
    public required AIAgent Agent { get; init; }

    public AgentSession? Session { get; init; }

    public required IReadOnlyList<ChatMessage> SeedMessages { get; init; }

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    public async ValueTask DisposeAsync()
    {
        if (Session is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }
}
