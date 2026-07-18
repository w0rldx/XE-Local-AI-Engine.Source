namespace XE_Local_AI_Engine.Tests.Testing;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Builds a <see cref="NodeChatInvocationPump" /> for tests that exercise persistence/streaming behaviour. The pump
///     no longer owns the run-envelope write (it rides into the terminalize persistence command), so it needs only the
///     persistence service, a usage-provider resolver, and a clock. <paramref name="usageProvider" /> lets a test assert
///     the resolved provider round-trips onto the envelope row; it defaults to <c>unknown</c> for tests indifferent to it.
/// </summary>
internal static class ChatPumpTestFactory
{
    public static NodeChatInvocationPump Create(INodeChatPersistenceService persistence, string usageProvider = AgentUsageProviders.Unknown)
    {
        return new NodeChatInvocationPump(persistence, new StubUsageProviderResolver(usageProvider), TimeProvider.System);
    }

    // Always attributes the given provider, mirroring the real resolver's never-throw contract without touching the
    // cloud/local routing seams.
    private sealed class StubUsageProviderResolver(string provider) : IUsageProviderResolver
    {
        public Task<string> ResolveAsync(string? modelName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(provider);
        }
    }
}
