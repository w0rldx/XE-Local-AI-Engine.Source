namespace XE_Local_AI_Engine.Tests.Mcp;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Regression guard for the captive-dependency fix (HIGH): the singleton connection manager must NOT capture the
///     Scoped, DbContext-backed <see cref="IMcpServerStore" />. Building the container with scope + on-build validation
///     (as the host does in Development) and resolving the manager must not throw — it would if the singleton injected
///     the scoped store directly.
/// </summary>
public sealed class McpServerConnectionManagerDiTests
{
    [Test]
    public async Task Resolve_WithScopeAndBuildValidation_DoesNotThrowCaptiveDependency()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<McpOptions>();

        // The store is Scoped (DbContext-backed in production). A singleton that captured it would fail validation.
        services.AddScoped<IMcpServerStore, StubMcpServerStore>();
        services.AddSingleton<IMcpToolRegistry, McpToolRegistry>();
        services.AddSingleton<IMcpClientFactory, McpClientFactory>();
        services.AddSingleton<IMcpServerConnectionManager, McpServerConnectionManager>();

        // ValidateOnBuild + ValidateScopes mirrors the host in Development; the manager is IAsyncDisposable, so the
        // provider must be disposed asynchronously.
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        var manager = provider.GetRequiredService<IMcpServerConnectionManager>();

        AssertEx.NotNull(manager);
    }

    // A trivial Scoped store stand-in: the test only proves the container builds + resolves the singleton manager under
    // scope validation, so no method is ever invoked. (The interface is public; a hand-rolled stub avoids proxying the
    // internal IMcpClientFactory, which NSubstitute cannot do on this non-strong-named assembly.)
    private sealed class StubMcpServerStore : IMcpServerStore
    {
        public Task<McpServerRecord> AddAsync(McpServerInput input, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<McpServerRecord?> UpdateAsync(Guid id, McpServerInput input, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<McpServerRecord?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<McpServerRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<McpServerRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<McpServerRecord>> ListEnabledAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
