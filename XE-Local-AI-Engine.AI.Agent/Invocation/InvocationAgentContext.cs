namespace XE_Local_AI_Engine.AI.Agent.Invocation;

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
    /// <summary>The agent instance created for this invocation.</summary>
    public required AIAgent Agent { get; init; }

    /// <summary>Optional framework session; null for stateless single-turn invocations.</summary>
    public AgentSession? Session { get; init; }

    /// <summary>System and conversation messages used to seed the run.</summary>
    public required IReadOnlyList<ChatMessage> SeedMessages { get; init; }

    /// <summary>Provider/framework run options such as model id and reasoning settings.</summary>
    public AgentRunOptions? RunOptions { get; set; }

    /// <summary>Small metadata bag for runner diagnostics, such as resolved model id and tool enablement.</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    /// <summary>Disposes the underlying session when the framework created one.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Session is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }
}
