namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.EntityFrameworkCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     One EF Core internal service provider per host, shared by the node's DbContexts and disposed with the host. Registered only
///     when <c>EntityFramework:ServiceProviderCaching</c> is <c>false</c> (test hosts); production lets EF build and cache its own.
/// </summary>
/// <remarks>
///     Avoids both EF's immortal per-host cache entry and a provider rebuild per DbContext scope (docs/agent-knowledge.md §1). The
///     host's <see cref="ILoggerFactory" /> keeps EF logging on the host's sinks. EF accepts only singleton interceptors the provider
///     already holds; the materialization one ignores every context but <see cref="NodeChatDbContext" />, so the identity context shares it.
/// </remarks>
internal sealed class NodeEfInternalServices : IDisposable
{
    public NodeEfInternalServices(ILoggerFactory loggerFactory, NodeEncryptionMaterializationInterceptor materializationInterceptor)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(materializationInterceptor);
        Provider = new ServiceCollection().AddEntityFrameworkSqlite()
                                          .AddSingleton(loggerFactory)
                                          .AddSingleton<ISingletonInterceptor>(materializationInterceptor)
                                          .BuildServiceProvider();
    }

    public ServiceProvider Provider { get; }

    public void Dispose() => Provider.Dispose();
}
