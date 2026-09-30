namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval.Live;

using Microsoft.Extensions.Logging;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     The live eval's ingestion path, model-free: repository-imported documents must carry production metadata
///     (source path, content kind, language, symbol) into the hits, or the code-path and code-symbol categories measure
///     nothing.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class RetrievalEvalLiveIngestionTests : IDisposable
{
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task RepositoryImport_CodeCopy_IsIndexedAsCSharpWithSourcePathAndSymbol()
    {
        _ = Directory.CreateDirectory(_rootPath);
        RetrievalEvalCorpus.FixtureDocument[] documents =
        [
            new()
            {
                Key = "code/QuasarWidget.cs.txt",
                Body = "public sealed class QuasarWidget\n{\n    public int Spin(int turns) => turns * 2;\n}\n"
            },
            new()
            {
                Key = "en/quasar-notes.md",
                Body = "# Quasar notes\n\nThe quasar widget spins twice per turn.\n"
            }
        ];
        var options = new KnowledgeBaseOptions
        {
            EmbeddingModelName = DeterministicEmbeddingProvider.ModelName
        };

        using var fixture = await RetrievalEvalFixture.BuildAsync(Path.Combine(_rootPath, "live-ingest.sqlite"),
            _keyHolder,
            documents,
            new DeterministicEmbeddingProvider(64, new Dictionary<string, string>(StringComparer.Ordinal)),
            options,
            LiveCorpusView.SourcePathOf,
            CancellationToken.None);

        var result = await fixture.CreateSearchService(options, Substitute.For<IRerankerClient>())
                                  .SearchAsync(new KnowledgeSearchRequest
                                  {
                                      Query = "QuasarWidget",
                                      Limit = 5
                                  }, CancellationToken.None);

        var code = AssertEx.NotNull(result.Results.FirstOrDefault(hit => hit.DocumentId == fixture.DocumentIdsByKey["code/QuasarWidget.cs.txt"]),
            "the code document must be retrievable by its class name.");
        AssertEx.Equal("code/QuasarWidget.cs", code.SourcePath);
        AssertEx.Equal("code", code.ContentKind);
        AssertEx.Equal("csharp", code.Language);
        AssertEx.Equal("QuasarWidget", code.Symbol);

        var prose = AssertEx.NotNull(result.Results.FirstOrDefault(hit => hit.DocumentId == fixture.DocumentIdsByKey["en/quasar-notes.md"]));
        AssertEx.Equal("en/quasar-notes.md", prose.SourcePath);
        AssertEx.Equal("markup", prose.ContentKind);
    }

    [Test]
    public async Task QueryEmbeddingFailure_IsCountedByTheCapturingLogger_AndVoidsTheRow()
    {
        _ = Directory.CreateDirectory(_rootPath);
        var options = new KnowledgeBaseOptions
        {
            EmbeddingModelName = DeterministicEmbeddingProvider.ModelName
        };

        // Embeds during ingestion, then fails at query time the way a crashed embedding server does.
        var real = new DeterministicEmbeddingProvider(64, new Dictionary<string, string>(StringComparer.Ordinal));
        var failQueries = false;
        var provider = Substitute.For<ILocalModelProvider>();
        _ = provider.ProviderName.Returns(real.ProviderName);
        _ = provider.ListModelsAsync(Arg.Any<CancellationToken>()).Returns(call => real.ListModelsAsync(call.Arg<CancellationToken>()));
        _ = provider.CreateEmbeddingGenerator(Arg.Any<LocalModelSelection>())
                    .Returns(call => failQueries ? throw new HttpRequestException("embedding server down") : real.CreateEmbeddingGenerator(call.Arg<LocalModelSelection>()));

        using var fixture = await RetrievalEvalFixture.BuildAsync(Path.Combine(_rootPath, "live-degrade.sqlite"),
            _keyHolder,
            [
                new RetrievalEvalCorpus.FixtureDocument
                {
                    Key = "en/quasar-notes.md",
                    Body = "# Quasar notes\n\nThe quasar widget spins twice per turn.\n"
                }
            ],
            provider,
            options,
            LiveCorpusView.SourcePathOf,
            CancellationToken.None);
        failQueries = true;
        var logger = new QueryEmbeddingDegradeLogger();
        // Same level and field name, other EventId: only the product's stable EventId counts as a degrade.
        logger.LogWarning("Unrelated warning. Exception type: {ExceptionType}.", nameof(IOException));

        var result = await fixture.CreateSearchService(options, Substitute.For<IRerankerClient>(), logger)
                                  .SearchAsync(new KnowledgeSearchRequest
                                  {
                                      Query = "quasar widget",
                                      Limit = 5
                                  }, CancellationToken.None);

        AssertEx.True(result.Results.Count > 0, "the search must still answer, lexical-only.");
        AssertEx.True(logger.ExceptionTypes.SequenceEqual([nameof(HttpRequestException)]), string.Join(", ", logger.ExceptionTypes));
        var empty = LiveMetricSummary.From([]);
        var row = RetrievalEvalLiveTests.WithQueryEmbeddingDegrades(new LiveConfigResult
        {
            Id = "F0",
            Description = "d",
            Valid = true,
            Overall = empty,
            ByCategory = new Dictionary<string, LiveMetricSummary>(),
            ByLanguage = new Dictionary<string, LiveMetricSummary>(),
            EnglishOnly = empty,
            EndToEnd = LiveLatency.From([]),
            RerankStage = LiveLatency.From([])
        }, logger.ExceptionTypes.Count);
        AssertEx.False(row.Valid);
        AssertEx.Equal(1, row.QueryEmbeddingDegrades);
        AssertEx.Equal("query embedding degraded to lexical-only on 1 queries", row.InvalidReason);
    }
}
