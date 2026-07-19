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
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

// AddServices builds the full service graph with ValidateOnBuild, which eagerly constructs NodeOperatorSecretProvider and
// so resolves the operator secret. That secret is read process-env-first (XE_NODE_SQLITE_KEY) before configuration, so
// this class shares the same process-global resource as DesktopBootstrapTests and the CUDA env-scrub test. The shared
// NotInParallel key serializes them so a concurrent env mutation can neither remove the key this test supplies nor leak an
// invalid one into it; the key itself is provided via in-memory configuration below rather than by mutating the process
// environment, so this class only reads the resource and never poisons a sibling.
[NotInParallel("XE_NODE_SQLITE_KEY")]
public sealed class PlaybookRetrievalRankerRegistrationTests : IDisposable
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
            // ValidateOnBuild eagerly constructs NodeOperatorSecretProvider, which requires a base64 32-byte operator
            // secret. Supply one via configuration so the build never depends on an ambient/leaked process env var.
            [NodeOperatorSecretProvider.EnvVarName] = Convert.ToBase64String(new byte[NodeOperatorSecretProvider.ExpectedSecretLength]),
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
