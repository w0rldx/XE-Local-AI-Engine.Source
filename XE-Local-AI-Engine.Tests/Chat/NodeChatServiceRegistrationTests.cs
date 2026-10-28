namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class NodeChatServiceRegistrationTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task AddServices_RegistersNodeChatPersistenceServicesWithValidateScopes()
    {
        var builder = CreateBuilder();
        builder.AddServices(builder.Configuration, NodeStartupSettings.Read(builder.Configuration, builder.Environment));
        builder.Services.AddSingleton<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();

        await using var provider = builder.Services.BuildServiceProvider(true);

        var writer = provider.GetRequiredService<NodeChatPersistenceWriter>();
        var persistence = provider.GetRequiredService<INodeChatPersistenceService>();
        var restartRecovery = provider.GetRequiredService<NodeChatRestartRecoveryService>();
        var timeProvider = provider.GetRequiredService<TimeProvider>();
        var firstContextId = await writer.ExecuteConversationExclusiveAsync(Guid.NewGuid(),
            (dbContext, _) => Task.FromResult(dbContext.ContextId.InstanceId));
        var secondContextId = await writer.ExecuteConversationExclusiveAsync(Guid.NewGuid(),
            (dbContext, _) => Task.FromResult(dbContext.ContextId.InstanceId));

        AssertEx.NotNull(persistence);
        AssertEx.NotNull(restartRecovery);
        AssertEx.NotNull(timeProvider);
        // Registered, or a thin run envelope silently stays booked under "unknown"; lazy, so resolving it builds nothing yet.
        AssertEx.NotNull(provider.GetService<Lazy<IUsageProviderResolver>>());
        AssertEx.NotEqual(firstContextId, secondContextId, "Registered writer should resolve a fresh NodeChatDbContext for each persistence operation.");
    }

    // Without the reader a provider context-size rejection is never recovered, and nothing else would notice.
    [Test]
    public async Task AddServices_HandsTheProviderErrorBodyReaderToTheProviderCallBudgetHop()
    {
        var builder = CreateBuilder();
        builder.AddServices(builder.Configuration, NodeStartupSettings.Read(builder.Configuration, builder.Environment));
        builder.Services.AddSingleton<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();

        await using var host = builder.Services.BuildServiceProvider(true);
        var reader = host.GetRequiredService<IProviderErrorBodyReader>();

        // The host's IChatClient needs the node secret to resolve, so the same decoration runs over a stub provider client.
        var pipeline = new ServiceCollection();
        _ = pipeline.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        _ = pipeline.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _ = pipeline.AddSingleton(Substitute.For<IChatClient>());
        _ = pipeline.AddSingleton(reader);
        _ = pipeline.DecorateChatClientPipeline();
        await using var agentRuntime = pipeline.BuildServiceProvider();

        var budgetHop = AssertEx.NotNull(agentRuntime.GetRequiredService<IChatClient>().GetService<ProviderCallBudgetChatClient>());
        AssertEx.True(reader is ProviderErrorBodyReader, "The host must register its ClientResultException body reader.");
        AssertEx.True(ReferenceEquals(reader, budgetHop.ErrorBodyReader), "The provider-call budget hop must receive the registered error-body reader.");
    }

    [Test]
    public void AddServices_DoesNotRegisterNodeChatEndpoints()
    {
        var builder = CreateBuilder();
        builder.AddServices(builder.Configuration, NodeStartupSettings.Read(builder.Configuration, builder.Environment));

        var localChatHubRegistration = builder.Services.FirstOrDefault(descriptor => descriptor.ServiceType.FullName?.Contains("LocalChatHub", StringComparison.Ordinal) == true);

        AssertEx.Null(localChatHubRegistration);
    }

    /// <summary>The Aspire integration's shared-cache string reaches no consumer; every reader of the key gets a private cache.</summary>
    [Test]
    public async Task AddServices_RewritesASharedCacheNodeConnectionStringToAPrivateCache()
    {
        var builder = CreateBuilder(";Cache=Shared;Mode=ReadWriteCreate");
        builder.AddServices(builder.Configuration, NodeStartupSettings.Read(builder.Configuration, builder.Environment));
        builder.Services.AddSingleton<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();

        await using var provider = builder.Services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();

        foreach (var connectionString in new[]
                 {
                     builder.Configuration.GetConnectionString("node-sqlite"),
                     scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.GetConnectionString(),
                     scope.ServiceProvider.GetRequiredService<NodeIdentityDbContext>().Database.GetConnectionString()
                 })
        {
            AssertEx.False(AssertEx.NotNull(connectionString).Contains("Cache", StringComparison.OrdinalIgnoreCase), $"Shared cache survived in '{connectionString}'.");
        }
    }

    private WebApplicationBuilder CreateBuilder(string connectionStringSuffix = "")
    {
        var databasePath = GetDatabasePath("registration.sqlite");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = Directory.GetCurrentDirectory()
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:LocalChat:DefaultModel"] = "llama3.2",
            ["ConnectionStrings:node-sqlite"] = $"Data Source={databasePath}" + connectionStringSuffix,
            ["Ollama:Endpoint"] = "http://127.0.0.1:11434"
        });

        return builder;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
