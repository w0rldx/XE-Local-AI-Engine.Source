namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion.Implementation;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;

/// <summary>
///     One labeled query of the file-based live corpus: the harness label plus the reporting dimensions
///     <see cref="LabeledQuery" /> does not carry. <see cref="LabeledQuery.ScenarioGroup" /> repeats the category so a
///     harness-only consumer can still group by it.
/// </summary>
internal sealed class LiveCorpusQuery
{
    /// <summary>The label the harness scores.</summary>
    public required LabeledQuery Labeled { get; init; }

    /// <summary>One of <see cref="RetrievalEvalLiveCorpus.Categories" />.</summary>
    public required string Category { get; init; }

    /// <summary>The language the query is written in (<c>en</c> or <c>de</c>), not the answer's.</summary>
    public required string Language { get; init; }

    /// <summary>Topic-near documents that share the query's words without answering it (lexical-distractor only).</summary>
    public IReadOnlyList<string> DistractorKeys { get; init; } = [];

    /// <summary>
    ///     The two halves of a chunk-boundary fact, in document order: both live in the single relevant document, and
    ///     <see cref="RetrievalEvalLiveCorpus.VerifyBoundaryPhrasesSpanChunksAsync" /> proves no production chunk holds
    ///     both. A scorer can require both halves in the top-k. Empty for every other category.
    /// </summary>
    public IReadOnlyList<string> BoundaryPhrases { get; init; } = [];
}

/// <summary>
///     Loads the file-based corpus: each file under <c>documents/</c> is a document keyed by its relative path, and
///     <c>queries.json</c> holds the labels. <see cref="CorpusDirectoryVariable" /> points it at a private corpus.
/// </summary>
/// <remarks>
///     A corpus that cannot be scored as labeled fails the load with the offending query or key named, so a typo in a
///     label never turns into a silently lower metric. Layout, categories and labeling rules: <c>LiveCorpus/README.md</c>.
/// </remarks>
internal sealed class RetrievalEvalLiveCorpus
{
    /// <summary>Overrides the corpus directory, e.g. for a private, never-committed corpus.</summary>
    public const string CorpusDirectoryVariable = "XE_RETRIEVAL_EVAL_CORPUS_DIR";

    public const string NoAnswerCategory = "no-answer";
    public const string ChunkBoundaryCategory = "chunk-boundary";
    public const string LexicalDistractorCategory = "lexical-distractor";
    public const string LexicallyDisjointCategory = "lexically-disjoint-semantic";

    private const string DocumentsFolder = "documents";
    private const string QueriesFile = "queries.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // A misspelled label field ("relevent") must fail the load, not silently read as "no relevant documents".
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private RetrievalEvalLiveCorpus(string directoryPath,
        IReadOnlyList<RetrievalEvalCorpus.FixtureDocument> documents,
        IReadOnlyList<LiveCorpusQuery> queries)
    {
        DirectoryPath = directoryPath;
        Documents = documents;
        Queries = queries;
    }

    public static IReadOnlyList<string> Categories { get; } =
    [
        "en-prose",
        "de-prose",
        "cross-language",
        "code-exact-symbol",
        "code-path",
        "code-conceptual",
        "mixed-docs-code",
        "long-document",
        ChunkBoundaryCategory,
        "multi-source",
        NoAnswerCategory,
        LexicallyDisjointCategory,
        LexicalDistractorCategory
    ];

    public static IReadOnlyList<string> Languages { get; } = ["en", "de"];

    /// <summary>The copy of the committed corpus the test project places next to its assembly.</summary>
    public static string CommittedDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Knowledge", "RetrievalEval", "LiveCorpus");

    public string DirectoryPath { get; }

    public IReadOnlyList<RetrievalEvalCorpus.FixtureDocument> Documents { get; }

    public IReadOnlyList<LiveCorpusQuery> Queries { get; }

    public IReadOnlyList<LabeledQuery> LabeledQueries => [.. Queries.Select(static query => query.Labeled)];

    /// <summary>The override directory when <see cref="CorpusDirectoryVariable" /> is set, otherwise the committed corpus.</summary>
    public static string ResolveDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(CorpusDirectoryVariable);
        return string.IsNullOrWhiteSpace(overrideDirectory) ? CommittedDirectory : overrideDirectory;
    }

    public static RetrievalEvalLiveCorpus Load() =>
        Load(ResolveDirectory());

    public static RetrievalEvalLiveCorpus Load(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        var documentsPath = Path.Combine(directoryPath, DocumentsFolder);
        var queriesPath = Path.Combine(directoryPath, QueriesFile);
        if (!Directory.Exists(documentsPath) || !File.Exists(queriesPath))
        {
            throw Invalid(directoryPath, $"expected a '{DocumentsFolder}/' folder and a '{QueriesFile}' file.");
        }

        var documents = LoadDocuments(directoryPath, documentsPath);
        var bodiesByKey = documents.ToDictionary(static document => document.Key, static document => document.Body, StringComparer.Ordinal);

        QueryFile? file;
        try
        {
            file = JsonSerializer.Deserialize<QueryFile>(File.ReadAllText(queriesPath), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw Invalid(directoryPath, $"{QueriesFile} is malformed: {exception.Message}");
        }

        if (file?.Queries is not { Count: > 0 } entries)
        {
            throw Invalid(directoryPath, $"{QueriesFile} contains no queries.");
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var queries = new List<LiveCorpusQuery>(entries.Count);
        foreach (var entry in entries)
        {
            queries.Add(ToQuery(directoryPath, entry, bodiesByKey, seenIds));
        }

        return new RetrievalEvalLiveCorpus(directoryPath, documents, queries);
    }

    /// <summary>
    ///     Proves every chunk-boundary label: runs each labeled document through the real extractor and the production
    ///     chunker (the <see cref="KnowledgeBaseOptions" /> defaults) and requires the two boundary phrases to land in
    ///     different chunks, with no single chunk holding both.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="Load(string)" /> because extraction is asynchronous. The live run awaits it on the
    ///     selected corpus before ingestion. <paramref name="sourcePathOf" /> maps a key to the path ingestion imports it
    ///     under, whose extension selects the reader.
    /// </remarks>
    public async Task VerifyBoundaryPhrasesSpanChunksAsync(Func<string, string> sourcePathOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePathOf);
        var chunker = new HeaderBoundaryChunkingService(Options.Create(new KnowledgeBaseOptions()));
        var extractor = new DocumentTextExtractor(NullLogger<DocumentTextExtractor>.Instance);
        var bodiesByKey = Documents.ToDictionary(static document => document.Key, static document => document.Body, StringComparer.Ordinal);

        foreach (var query in Queries.Where(static query => query.BoundaryPhrases.Count == 2))
        {
            var id = query.Labeled.Id;
            var key = query.Labeled.RelevantDocumentKeys[0];
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(bodiesByKey[key]));
            var sourcePath = sourcePathOf(key);
            var extraction = await extractor.ExtractStructuredAsync(stream, sourcePath, Path.GetExtension(sourcePath), cancellationToken);
            if (extraction.Document is null)
            {
                throw Invalid(DirectoryPath, $"query '{id}': document '{key}' did not extract ({extraction.Status}).");
            }

            var chunks = chunker.Chunk(extraction.Document).Chunks.Select(static chunk => Normalize(chunk.Content)).ToList();
            var first = Normalize(query.BoundaryPhrases[0]);
            var second = Normalize(query.BoundaryPhrases[1]);
            if (!chunks.Any(chunk => chunk.Contains(first, StringComparison.Ordinal))
                || !chunks.Any(chunk => chunk.Contains(second, StringComparison.Ordinal)))
            {
                throw Invalid(DirectoryPath, $"query '{id}': a boundary phrase is not contained whole in any chunk of '{key}'.");
            }

            if (chunks.Any(chunk => chunk.Contains(first, StringComparison.Ordinal) && chunk.Contains(second, StringComparison.Ordinal)))
            {
                throw Invalid(DirectoryPath, $"query '{id}': one chunk of '{key}' holds both boundary phrases, so the fact does not span a chunk boundary.");
            }
        }
    }

    /// <summary>Lowercased word tokens joined by single spaces — the same normalization the harness anchors use.</summary>
    public static string Normalize(string value) =>
        string.Join(' ', RetrievalTokens.Split(value));

    private static List<RetrievalEvalCorpus.FixtureDocument> LoadDocuments(string directoryPath, string documentsPath)
    {
        var documents = Directory.EnumerateFiles(documentsPath, "*", SearchOption.AllDirectories)
                                 .Select(path => new RetrievalEvalCorpus.FixtureDocument
                                 {
                                     Key = Path.GetRelativePath(documentsPath, path).Replace('\\', '/'),
                                     Body = File.ReadAllText(path)
                                 })
                                 .OrderBy(static document => document.Key, StringComparer.Ordinal)
                                 .ToList();
        if (documents.Count == 0)
        {
            throw Invalid(directoryPath, $"'{DocumentsFolder}/' contains no documents.");
        }

        var empty = documents.FirstOrDefault(static document => string.IsNullOrWhiteSpace(document.Body));
        if (empty is not null)
        {
            throw Invalid(directoryPath, $"document '{empty.Key}' is empty.");
        }

        return documents;
    }

    private static LiveCorpusQuery ToQuery(string directoryPath,
        QueryEntry entry,
        IReadOnlyDictionary<string, string> bodiesByKey,
        HashSet<string> seenIds)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            throw Invalid(directoryPath, "a query has no id.");
        }

        var id = entry.Id;
        if (!seenIds.Add(id))
        {
            throw Invalid(directoryPath, $"query id '{id}' is used more than once.");
        }

        if (string.IsNullOrWhiteSpace(entry.Query))
        {
            throw Invalid(directoryPath, $"query '{id}' has no query text.");
        }

        if (entry.Category is null || !Categories.Contains(entry.Category, StringComparer.Ordinal))
        {
            throw Invalid(directoryPath, $"query '{id}' has unknown category '{entry.Category}'; expected one of: {string.Join(", ", Categories)}.");
        }

        if (entry.Language is null || !Languages.Contains(entry.Language, StringComparer.Ordinal))
        {
            throw Invalid(directoryPath, $"query '{id}' has unknown language '{entry.Language}'; expected one of: {string.Join(", ", Languages)}.");
        }

        if (entry.Answerable is not bool answerable)
        {
            throw Invalid(directoryPath, $"query '{id}' must state 'answerable'.");
        }

        if (answerable == string.Equals(entry.Category, NoAnswerCategory, StringComparison.Ordinal))
        {
            throw Invalid(directoryPath, $"query '{id}': 'answerable' must be false exactly for the '{NoAnswerCategory}' category.");
        }

        var relevant = entry.Relevant ?? [];
        var anchors = entry.Anchors ?? [];
        var distractors = entry.Distractors ?? [];
        var boundary = entry.BoundaryPhrases ?? [];
        var citation = entry.Citation ?? string.Empty;

        RequireKnownKeys(directoryPath, id, "relevant", relevant, bodiesByKey);
        RequireKnownKeys(directoryPath, id, "distractors", distractors, bodiesByKey);
        if (relevant.Distinct(StringComparer.Ordinal).Count() != relevant.Count)
        {
            throw Invalid(directoryPath, $"query '{id}' lists a relevant document twice.");
        }

        if (!answerable)
        {
            if (relevant.Count > 0 || citation.Length > 0 || anchors.Count > 0)
            {
                throw Invalid(directoryPath, $"no-answer query '{id}' must not carry relevant documents, a citation or anchors.");
            }
        }
        else
        {
            if (relevant.Count == 0)
            {
                throw Invalid(directoryPath, $"answerable query '{id}' labels no relevant document.");
            }

            var relevantBodies = relevant.Select(key => bodiesByKey[key]).ToList();
            RequirePhrase(directoryPath, id, "citation", citation, relevantBodies);
            foreach (var phrase in boundary)
            {
                RequirePhrase(directoryPath, id, "boundary phrase", phrase, relevantBodies);
            }

            foreach (var anchor in anchors)
            {
                if (!relevantBodies.Any(body => HeadingsOf(body).Any(heading => heading.Contains(Normalize(anchor), StringComparison.Ordinal))))
                {
                    throw Invalid(directoryPath, $"query '{id}': anchor '{anchor}' is not a heading of any relevant document.");
                }
            }
        }

        if (distractors.Any(relevant.Contains))
        {
            throw Invalid(directoryPath, $"query '{id}' lists a document as both relevant and distractor.");
        }

        var isBoundary = string.Equals(entry.Category, ChunkBoundaryCategory, StringComparison.Ordinal);
        if (isBoundary != (boundary.Count > 0) || (isBoundary && (boundary.Count != 2 || relevant.Count != 1)))
        {
            throw Invalid(directoryPath, $"query '{id}': exactly the '{ChunkBoundaryCategory}' category carries a 'boundaryPhrases' pair of two phrases, over exactly one relevant document.");
        }

        var isDistractor = string.Equals(entry.Category, LexicalDistractorCategory, StringComparison.Ordinal);
        if (isDistractor != (distractors.Count > 0))
        {
            throw Invalid(directoryPath, $"query '{id}': exactly the '{LexicalDistractorCategory}' category names 'distractors'.");
        }

        var labeled = new LabeledQuery(id, entry.Query, relevant.Count > 0 ? relevant[0] : string.Empty, citation)
        {
            RelevantDocumentKeys = relevant,
            ExpectsNoAnswer = !answerable,
            SourceAnchors = anchors,
            ScenarioGroup = entry.Category
        };
        return new LiveCorpusQuery
        {
            Labeled = labeled,
            Category = entry.Category,
            Language = entry.Language,
            DistractorKeys = distractors,
            BoundaryPhrases = boundary
        };
    }

    private static void RequireKnownKeys(string directoryPath,
        string id,
        string field,
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, string> bodiesByKey)
    {
        var unknown = keys.FirstOrDefault(key => !bodiesByKey.ContainsKey(key));
        if (unknown is not null)
        {
            throw Invalid(directoryPath, $"query '{id}' names unknown document '{unknown}' in '{field}'.");
        }
    }

    // The harness scores a citation as a contiguous normalized phrase, so a label whose phrase no relevant document
    // contains could never score and is a labeling error.
    private static void RequirePhrase(string directoryPath, string id, string field, string phrase, IReadOnlyList<string> relevantBodies)
    {
        var normalized = Normalize(phrase);
        if (normalized.Length == 0 || !relevantBodies.Any(body => Normalize(body).Contains(normalized, StringComparison.Ordinal)))
        {
            throw Invalid(directoryPath, $"query '{id}': {field} '{phrase}' does not occur in any relevant document.");
        }
    }

    private static IEnumerable<string> HeadingsOf(string body) =>
        body.Split('\n')
            .Where(static line => line.TrimStart().StartsWith('#'))
            .Select(Normalize);

    private static InvalidDataException Invalid(string directoryPath, string reason) =>
        new($"Retrieval-eval corpus '{directoryPath}': {reason}");

    // Read back from queries.json, so no member is required. Internal, not private: only the deserializer sets them,
    // which S1144 reports as unused setters on a private type.
    internal sealed class QueryFile
    {
        public List<QueryEntry>? Queries { get; init; }
    }

    internal sealed class QueryEntry
    {
        public string? Id { get; init; }

        public string? Query { get; init; }

        public string? Language { get; init; }

        public string? Category { get; init; }

        public bool? Answerable { get; init; }

        public List<string>? Relevant { get; init; }

        public string? Citation { get; init; }

        public List<string>? Anchors { get; init; }

        public List<string>? Distractors { get; init; }

        public List<string>? BoundaryPhrases { get; init; }
    }
}
