namespace XE_Local_AI_Engine.Tests.Testing;

using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     A ready-made <see cref="IServiceScopeFactory" /> for <c>LocalToolOfferProvider</c> tests. Its scopes resolve an
///     always-empty <see cref="ICustomToolCatalog" />, so the provider's synchronous offer paths (which the vast majority
///     of these tests exercise) construct without a custom-tool dependency, and the async paths — if a test calls one —
///     simply see no custom tools. Backed by a real service provider so the scope lifecycle behaves exactly as production.
/// </summary>
internal static class NullCustomToolScopeFactory
{
    public static IServiceScopeFactory Instance { get; } =
        new ServiceCollection()
            .AddScoped<ICustomToolCatalog, EmptyCustomToolCatalog>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    private sealed class EmptyCustomToolCatalog : ICustomToolCatalog
    {
        public Task<IReadOnlyList<LocalChatToolDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalChatToolDescriptor>>([]);
        }

        public Task<IReadOnlyDictionary<string, AITool>> TryResolveManyAsync(IReadOnlyCollection<string> names,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<string, AITool>>(ReadOnlyDictionary<string, AITool>.Empty);
        }
    }
}
