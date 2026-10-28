namespace XE_Local_AI_Engine.Tests.Testing;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Builds a <see cref="NodeChatInvocationPump" /> for persistence/streaming tests. The pump does not own the
///     run-envelope write (it rides the terminalize command), so it needs only persistence, a usage-provider resolver
///     and a clock.
/// </summary>
/// <remarks><c>usageProvider</c> lets a test assert the envelope row's provider (default <c>unknown</c>).</remarks>
internal static class ChatPumpTestFactory
{
    public static NodeChatInvocationPump Create(INodeChatPersistenceService persistence, string usageProvider = AgentUsageProviders.Unknown)
    {
        return new NodeChatInvocationPump(persistence, new StubUsageProviderResolver(usageProvider), TimeProvider.System);
    }

    // Always attributes the given provider, mirroring the real resolver's never-throw contract without touching the
    // cloud/local routing seams.
    private sealed class StubUsageProviderResolver : IUsageProviderResolver
    {
        private readonly string _provider;

        public StubUsageProviderResolver(string provider)
        {
            _provider = provider;
        }

        public Task<string> ResolveAsync(string? modelName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_provider);
        }
    }
}
