namespace XE_Local_AI_Engine.Tests.Testing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;

/// <summary>
///     Builds a <see cref="NodeChatInvocationPump" /> for tests that exercise persistence/streaming behaviour and do not
///     care about the durable run-envelope ledger. Supplies a no-op <see cref="IAgentExecutionLogStore" /> behind a real
///     <see cref="IServiceScopeFactory" /> so the pump's best-effort ledger write is a harmless no-op.
/// </summary>
internal static class ChatPumpTestFactory
{
    public static NodeChatInvocationPump Create(INodeChatPersistenceService persistence)
    {
        var provider = new ServiceCollection()
                       .AddSingleton<IAgentExecutionLogStore, NoOpAgentExecutionLogStore>()
                       .BuildServiceProvider();

        return new NodeChatInvocationPump(persistence,
            TimeProvider.System,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodeChatInvocationPump>.Instance);
    }

    private sealed class NoOpAgentExecutionLogStore : IAgentExecutionLogStore
    {
        public Task<AgentExecutionLogRecord> AddAsync(AgentExecutionLogInput input, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("The chat pump never writes adaptive-memory diagnostics rows.");
        }

        public Task AddRunEnvelopeAsync(AgentRunEnvelopeInput input, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentExecutionLogRecord>> ListByAgentAsync(Guid agentDefinitionId, int limit, int offset = 0, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AgentExecutionLogRecord>>([]);
        }

        public Task<int> DeleteOlderThanAsync(long cutoffEpochMs, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> TrimToMaxPerAgentAsync(int maxPerAgent, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }
    }
}
