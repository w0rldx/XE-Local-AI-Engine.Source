namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using System.Security.Cryptography;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval.Live;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Label integrity of the committed live corpus and the loader's failure contract. Nothing here scores retrieval:
///     these tests keep the labels honest so the opt-in real-model run measures what each category claims to measure.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class RetrievalEvalLiveCorpusTests : IDisposable
{
    private const int MinQueriesPerCategory = 5;

    private const string ValidQueries =
        """{"queries":[{"id":"p-1","query":"alpha","language":"en","category":"en-prose","answerable":true,"relevant":["private/alpha.md"],"citation":"alpha beta"}]}""";

    // Cross-language queries that keep a lexical bridge on purpose; LiveCorpus/README.md names them.
    private static readonly HashSet<string> DeliberateCrossLanguageBridges = new(StringComparer.Ordinal)
    {
        "cl-05"
    };

    // Function words long enough to pass the content-token length cut; everything else of four or more letters is a
    // content word for the lexical-overlap checks below.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "what",
        "when",
        "which",
        "where",
        "does",
        "have",
        "with",
        "that",
        "this",
        "from",
        "into",
        "should",
        "would",
        "much",
        "many",
        "long",
        "after",
        "before",
        "must",
        "each",
        "every",
        "then",
        "than",
        "until",
        "their",
        "there",
        "welche",
        "welcher",
        "wann",
        "eine",
        "einer",
        "eines",
        "einem",
        "einen",
        "sich",
        "nach",
        "wird",
        "werden",
        "sind",
        "oder",
        "auch",
        "wenn",
        "dass",
        "meines",
        "meiner",
        "meine",
        "während"
    };

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "retrieval-eval-corpus-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public void CommittedCorpus_Loads_WithUniqueIdsAndKnownKeys()
    {
        var corpus = RetrievalEvalLiveCorpus.Load(RetrievalEvalLiveCorpus.CommittedDirectory);
        var keys = corpus.Documents.Select(static document => document.Key).ToHashSet(StringComparer.Ordinal);

        AssertEx.True(corpus.Documents.Count >= 40, $"The committed corpus shrank to {corpus.Documents.Count} documents.");
        AssertEx.True(corpus.Queries.Count >= 90, $"The committed corpus shrank to {corpus.Queries.Count} queries.");
        AssertEx.Equal(corpus.Queries.Count, corpus.Queries.Select(static query => query.Labeled.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var query in corpus.Queries)
        {
            foreach (var key in query.Labeled.RelevantDocumentKeys.Concat(query.DistractorKeys))
            {
                AssertEx.True(keys.Contains(key), $"Query '{query.Labeled.Id}' names unknown document '{key}'.");
            }
        }
    }

    [Test]
    public void CommittedCorpus_PopulatesEveryCategory_AndBothLanguages()
    {
        var corpus = RetrievalEvalLiveCorpus.Load(RetrievalEvalLiveCorpus.CommittedDirectory);

        foreach (var category in RetrievalEvalLiveCorpus.Categories)
        {
            var count = corpus.Queries.Count(query => string.Equals(query.Category, category, StringComparison.Ordinal));
            AssertEx.True(count >= MinQueriesPerCategory, $"Category '{category}' has {count} queries; at least {MinQueriesPerCategory} are required.");
        }

        foreach (var language in RetrievalEvalLiveCorpus.Languages)
        {
            AssertEx.True(corpus.Queries.Any(query => string.Equals(query.Language, language, StringComparison.Ordinal)),
                $"No query is written in '{language}'.");
        }
    }

    [Test]
    public void CommittedCorpus_NoAnswerQueries_LabelNothing_AndAnswerableQueriesLabelSomething()
    {
        var corpus = RetrievalEvalLiveCorpus.Load(RetrievalEvalLiveCorpus.CommittedDirectory);

        foreach (var query in corpus.Queries)
        {
            var labeled = query.Labeled;
            if (labeled.ExpectsNoAnswer)
            {
                AssertEx.Empty(labeled.RelevantDocumentKeys, $"No-answer query '{labeled.Id}' must not label relevant documents.");
            }
            else
            {
                AssertEx.True(labeled.RelevantDocumentKeys.Count > 0, $"Answerable query '{labeled.Id}' labels no relevant document.");
            }
        }
    }

    [Test]
    public void CommittedCorpus_CrossLanguageQueries_AskInTheOtherLanguage()
    {
        var corpus = RetrievalEvalLiveCorpus.Load(RetrievalEvalLiveCorpus.CommittedDirectory);
        var bodies = BodiesByKey(corpus);
        var crossLanguage = corpus.Queries.Where(static query => string.Equals(query.Category, "cross-language", StringComparison.Ordinal)).ToList();

        foreach (var query in crossLanguage)
        {
            // The corpus keeps each prose language in its own folder, so the folder of the answer names its language.
            var answerKey = query.Labeled.RelevantDocumentKeys.Single();
            AssertEx.False(string.Equals(answerKey[..2], query.Language, StringComparison.Ordinal),
                $"Cross-language query '{query.Labeled.Id}' is written in the language of its answer.");

            // A shared token (a loanword, a number) lets the lexical arm answer without crossing languages; only the
            // queries the README names as deliberate bridges may carry one.
            if (!DeliberateCrossLanguageBridges.Contains(query.Labeled.Id))
            {
                var answerTokens = RetrievalTokens.Split(bodies[answerKey]).ToHashSet(StringComparer.Ordinal);
                var bridges = RetrievalTokens.Split(query.Labeled.Text).Where(answerTokens.Contains).ToList();
                AssertEx.Empty(bridges, $"Cross-language query '{query.Labeled.Id}' shares tokens with its answer: {string.Join(", ", bridges)}.");
            }
        }

        AssertEx.True(
            crossLanguage.Any(static query => string.Equals(query.Language, "de", StringComparison.Ordinal)) &&
            crossLanguage.Any(static query => string.Equals(query.Language, "en", StringComparison.Ordinal)),
            "Cross-language queries must run in both directions.");
    }

    [Test]
    public void CommittedCorpus_LexicallyDisjointQueries_ShareNoContentWordWithTheirAnswer()
    {
        var corpus = RetrievalEvalLiveCorpus.Load(RetrievalEvalLiveCorpus.CommittedDirectory);
        var bodies = BodiesByKey(corpus);

        foreach (var query in corpus.Queries.Where(static query => string.Equals(query.Category, RetrievalEvalLiveCorpus.LexicallyDisjointCategory, StringComparison.Ordinal)))
        {
            foreach (var key in query.Labeled.RelevantDocumentKeys)
            {
                var documentTokens = RetrievalTokens.Split(bodies[key]).ToHashSet(StringComparer.Ordinal);
                var shared = ContentTokens(query.Labeled.Text).Where(documentTokens.Contains).ToList();
                AssertEx.Empty(shared, $"Query '{query.Labeled.Id}' shares content words with '{key}': {string.Join(", ", shared)}.");
            }
        }
    }

    [Test]
    public void CommittedCorpus_LexicalDistractors_RepeatTheQueryWordsMoreThanTheAnswer()
    {
        var corpus = RetrievalEvalLiveCorpus.Load(RetrievalEvalLiveCorpus.CommittedDirectory);
        var bodies = BodiesByKey(corpus);

        foreach (var query in corpus.Queries.Where(static query => string.Equals(query.Category, RetrievalEvalLiveCorpus.LexicalDistractorCategory, StringComparison.Ordinal)))
        {
            var queryTokens = ContentTokens(query.Labeled.Text).ToHashSet(StringComparer.Ordinal);
            var strongestAnswer = query.Labeled.RelevantDocumentKeys.Max(key => CountOccurrences(queryTokens, bodies[key]));
            foreach (var distractor in query.DistractorKeys)
            {
                var distractorCount = CountOccurrences(queryTokens, bodies[distractor]);
                AssertEx.True(distractorCount > strongestAnswer,
                    $"Query '{query.Labeled.Id}': distractor '{distractor}' repeats the query words {distractorCount} times, the answer {strongestAnswer}.");
            }
        }
    }

    [Test]
    public async Task CommittedCorpus_ChunkBoundaryFacts_SpanTwoChunksOfTheProductionChunker()
    {
        var corpus = RetrievalEvalLiveCorpus.Load(RetrievalEvalLiveCorpus.CommittedDirectory);
        AssertEx.True(corpus.Queries.Any(static query => query.BoundaryPhrases.Count == 2), "The committed corpus has no chunk-boundary query.");

        // Throws naming the query when a labeled fact does not straddle a production chunk boundary.
        await corpus.VerifyBoundaryPhrasesSpanChunksAsync(LiveCorpusView.SourcePathOf, CancellationToken.None);
    }

    [Test]
    public async Task VerifyBoundaryPhrases_BothHalvesInOneChunk_FailsNamingTheQuery()
    {
        WriteCorpus(
            """{"queries":[{"id":"p-1","query":"alpha","language":"en","category":"chunk-boundary","answerable":true,"relevant":["private/alpha.md"],"citation":"gamma","boundaryPhrases":["alpha","gamma"]}]}""");
        var corpus = RetrievalEvalLiveCorpus.Load(_rootPath);

        var exception = await AssertEx.ThrowsAsync<InvalidDataException>(() => corpus.VerifyBoundaryPhrasesSpanChunksAsync(LiveCorpusView.SourcePathOf, CancellationToken.None));

        AssertEx.True(exception.Message.Contains("query 'p-1': one chunk", StringComparison.Ordinal), exception.Message);
    }

    [Test]
    public void CommittedCorpus_PinnedCodeCopies_MatchTheirManifest()
    {
        var corpus = RetrievalEvalLiveCorpus.Load(RetrievalEvalLiveCorpus.CommittedDirectory);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(corpus.DirectoryPath, "code-manifest.json")));
        var entries = manifest.RootElement.GetProperty("pinnedCode").EnumerateArray().ToList();
        var codeKeys = corpus.Documents.Select(static document => document.Key)
                             .Where(static key => key.StartsWith("code/", StringComparison.Ordinal))
                             .ToHashSet(StringComparer.Ordinal);

        AssertEx.Equal(codeKeys.Count, entries.Count, "Every pinned code copy needs exactly one manifest entry.");
        foreach (var entry in entries)
        {
            var key = entry.GetProperty("key").GetString()!;
            AssertEx.True(codeKeys.Contains(key), $"Manifest entry '{key}' has no corpus document.");
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(corpus.DirectoryPath, "documents", key))));
            AssertEx.Equal(entry.GetProperty("sha256").GetString(), actual, $"Pinned copy '{key}' was edited; it must stay a verbatim copy of its origin.");
        }
    }

    [Test]
    [NotInParallel(RetrievalEvalLiveCorpus.CorpusDirectoryVariable)]
    public void ResolveDirectory_HonorsThePrivateCorpusOverride()
    {
        WriteCorpus(ValidQueries);
        var previous = Environment.GetEnvironmentVariable(RetrievalEvalLiveCorpus.CorpusDirectoryVariable);
        Environment.SetEnvironmentVariable(RetrievalEvalLiveCorpus.CorpusDirectoryVariable, _rootPath);
        try
        {
            var corpus = RetrievalEvalLiveCorpus.Load();

            AssertEx.Equal(_rootPath, corpus.DirectoryPath);
            AssertEx.Equal("private/alpha.md", corpus.Documents.Single().Key);
            AssertEx.Equal("p-1", corpus.Queries.Single().Labeled.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RetrievalEvalLiveCorpus.CorpusDirectoryVariable, previous);
        }
    }

    [Test]
    [Arguments("missing-key", """{"queries":[{"id":"p-1","query":"alpha","language":"en","category":"en-prose","answerable":true,"relevant":["private/missing.md"],"citation":"alpha"}]}""",
        "unknown document 'private/missing.md'")]
    [Arguments("duplicate-id",
        """{"queries":[{"id":"p-1","query":"alpha","language":"en","category":"en-prose","answerable":true,"relevant":["private/alpha.md"],"citation":"alpha"},{"id":"p-1","query":"beta","language":"en","category":"en-prose","answerable":true,"relevant":["private/alpha.md"],"citation":"beta"}]}""",
        "query id 'p-1' is used more than once")]
    [Arguments("unknown-category", """{"queries":[{"id":"p-1","query":"alpha","language":"en","category":"poetry","answerable":true,"relevant":["private/alpha.md"],"citation":"alpha"}]}""",
        "unknown category 'poetry'")]
    [Arguments("empty", """{"queries":[]}""", "contains no queries")]
    [Arguments("misspelled-field", """{"queries":[{"id":"p-1","query":"alpha","language":"en","category":"en-prose","answerable":true,"relevent":["private/alpha.md"],"citation":"alpha"}]}""",
        "is malformed")]
    [Arguments("no-answer-with-label", """{"queries":[{"id":"p-1","query":"alpha","language":"en","category":"no-answer","answerable":false,"relevant":["private/alpha.md"]}]}""",
        "must not carry relevant documents")]
    [Arguments("citation-not-in-answer", """{"queries":[{"id":"p-1","query":"alpha","language":"en","category":"en-prose","answerable":true,"relevant":["private/alpha.md"],"citation":"omega"}]}""",
        "citation 'omega' does not occur")]
    [Arguments("boundary-without-phrases",
        """{"queries":[{"id":"p-1","query":"alpha","language":"en","category":"chunk-boundary","answerable":true,"relevant":["private/alpha.md"],"citation":"alpha"}]}""",
        "carries a 'boundaryPhrases' pair")]
    public void Load_MalformedCorpus_FailsNamingTheProblem(string scenario, string queriesJson, string expectedMessage)
    {
        WriteCorpus(queriesJson);

        var exception = AssertEx.Throws<InvalidDataException>(() => RetrievalEvalLiveCorpus.Load(_rootPath), $"Scenario '{scenario}' must fail the load.");

        AssertEx.True(exception.Message.Contains(expectedMessage, StringComparison.Ordinal),
            $"Scenario '{scenario}' failed with an imprecise message: {exception.Message}");
    }

    [Test]
    public void Load_EmptyDocumentsFolder_Fails()
    {
        WriteCorpus(ValidQueries);
        File.Delete(Path.Combine(_rootPath, "documents", "private", "alpha.md"));

        var exception = AssertEx.Throws<InvalidDataException>(() => RetrievalEvalLiveCorpus.Load(_rootPath));

        AssertEx.True(exception.Message.Contains("contains no documents", StringComparison.Ordinal), exception.Message);
    }

    private void WriteCorpus(string queriesJson)
    {
        var documents = Path.Combine(_rootPath, "documents", "private");
        Directory.CreateDirectory(documents);
        File.WriteAllText(Path.Combine(documents, "alpha.md"), "# Alpha\n\nalpha beta gamma\n");
        File.WriteAllText(Path.Combine(_rootPath, "queries.json"), queriesJson);
    }

    private static Dictionary<string, string> BodiesByKey(RetrievalEvalLiveCorpus corpus) =>
        corpus.Documents.ToDictionary(static document => document.Key, static document => document.Body, StringComparer.Ordinal);

    private static IEnumerable<string> ContentTokens(string text) =>
        RetrievalTokens.Split(text).Where(static token => token.Length >= 4 && !StopWords.Contains(token)).Distinct(StringComparer.Ordinal);

    private static int CountOccurrences(IReadOnlySet<string> queryTokens, string body) =>
        RetrievalTokens.Split(body).Count(queryTokens.Contains);
}
