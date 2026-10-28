namespace XE_Local_AI_Engine.Tests.Testing;

using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     A ready-made <see cref="IServiceScopeFactory" /> for <c>LocalToolOfferProvider</c> tests whose scopes resolve an
///     always-empty <see cref="ICustomToolCatalog" />: synchronous offer paths construct without a custom-tool
///     dependency, and async paths see no custom tools.
/// </summary>
/// <remarks>Backed by a real service provider, so the scope lifecycle behaves exactly as in production.</remarks>
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
