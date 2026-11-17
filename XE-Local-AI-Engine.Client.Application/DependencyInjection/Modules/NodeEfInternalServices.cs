namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
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
        // Registered before AddEntityFrameworkSqlite, whose TryAdd then keeps it.
        Provider = new ServiceCollection().AddSingleton<IModelSource, ProcessSharedModelSource>()
                                          .AddEntityFrameworkSqlite()
                                          .AddSingleton(loggerFactory)
                                          .AddSingleton<ISingletonInterceptor>(materializationInterceptor)
                                          .BuildServiceProvider();
    }

    public ServiceProvider Provider { get; }

    public void Dispose() =>
        Provider.Dispose();

    /// <summary>
    ///     Hands every test host in the process the runtime model the first host built, sparing each later host the model build.
    /// </summary>
    /// <remarks>
    ///     Safe because neither context's model holds host state: no converter, comparer or convention captures a key, cipher, clock or
    ///     host service (docs/agent-knowledge.md §1). Retains one host's model and the EF singletons it was built with for the process,
    ///     never one per host. Design-time models stay per host and are built only on request.
    /// </remarks>
    private sealed class ProcessSharedModelSource : ModelSource
    {
        private static readonly ConcurrentDictionary<Type, IModel> RuntimeModels = new();

        public ProcessSharedModelSource(ModelSourceDependencies dependencies) : base(dependencies)
        {
        }

        public override IModel GetModel(DbContext context, ModelCreationDependencies modelCreationDependencies, bool designTime) =>
            designTime
                ? base.GetModel(context, modelCreationDependencies, designTime)
                : RuntimeModels.GetOrAdd(context.GetType(), _ => base.GetModel(context, modelCreationDependencies, designTime: false));
    }
}
