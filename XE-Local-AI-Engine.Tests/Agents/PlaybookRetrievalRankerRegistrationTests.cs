namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class PlaybookRetrievalRankerRegistrationTests : IDisposable
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
    public async Task AddServices_ResolvesBothEmbeddingRankerAndConcreteLexicalRanker()
    {
        var builder = CreateBuilder();
        builder.AddServices(builder.Configuration);
        builder.Services.AddSingleton<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();

        await using var provider = builder.Services.BuildServiceProvider(true);

        // The embedding ranker is the IPlaybookRetrievalRanker; the concrete lexical ranker must also resolve so the
        // embedding ranker can degrade to it (and the disabled-model path can delegate straight to it).
        var ranker = provider.GetRequiredService<IPlaybookRetrievalRanker>();
        var lexical = provider.GetRequiredService<LexicalPlaybookRetrievalRanker>();

        AssertEx.True(ranker is EmbeddingPlaybookRetrievalRanker, "The IPlaybookRetrievalRanker must resolve to the embedding ranker.");
        AssertEx.NotNull(lexical);
    }

    private WebApplicationBuilder CreateBuilder()
    {
        var databasePath = GetDatabasePath("ranker-registration.sqlite");
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
