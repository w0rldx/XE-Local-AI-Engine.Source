namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatServiceRegistrationTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    [Test]
    public async Task AddServices_RegistersNodeChatPhase44ServicesWithValidateScopes()
    {
        var builder = CreateBuilder();
        builder.AddServices(builder.Configuration);
        builder.Services.AddSingleton<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();

        await using var provider = builder.Services.BuildServiceProvider(true);

        var writer = provider.GetRequiredService<NodeChatPersistenceWriter>();
        var persistence = provider.GetRequiredService<INodeChatPersistenceService>();
        var restartRecovery = provider.GetRequiredService<NodeChatRestartRecoveryService>();
        var timeProvider = provider.GetRequiredService<TimeProvider>();
        var firstContextId = await writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(Guid.NewGuid()),
            (dbContext, _) => Task.FromResult(dbContext.ContextId.InstanceId)).ConfigureAwait(false);
        var secondContextId = await writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(Guid.NewGuid()),
            (dbContext, _) => Task.FromResult(dbContext.ContextId.InstanceId)).ConfigureAwait(false);

        AssertEx.NotNull(persistence);
        AssertEx.NotNull(restartRecovery);
        AssertEx.NotNull(timeProvider);
        AssertEx.NotEqual(firstContextId, secondContextId, "Registered writer should resolve a fresh NodeChatDbContext for each persistence operation.");
    }

    [Test]
    public void AddServices_DoesNotRegisterPhase45OrPhase46ChatEndpoints()
    {
        var builder = CreateBuilder();
        builder.AddServices(builder.Configuration);

        var localChatHubRegistration = builder.Services.FirstOrDefault(descriptor => descriptor.ServiceType.FullName?.Contains("LocalChatHub", StringComparison.Ordinal) == true);

        AssertEx.Null(localChatHubRegistration);
    }

    private WebApplicationBuilder CreateBuilder()
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
            ["CentralPlatform:BaseUrl"] = "https://127.0.0.1",
            ["ConnectionStrings:node-sqlite"] = $"Data Source={databasePath}",
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
