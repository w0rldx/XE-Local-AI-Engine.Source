namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval.Live;

/// <summary>Report grouping of one labelled query.</summary>
internal sealed class LiveQueryLabel
{
    public required string Category { get; init; }

    public required string Language { get; init; }

    public required IReadOnlyList<string> BoundaryPhrases { get; init; }

    public required bool EnglishOnly { get; init; }
}

/// <summary>
///     The one seam between the live eval and the labelled corpus loader (<see cref="RetrievalEvalLiveCorpus" />): the
///     documents to ingest, the labelled queries, and each query's category and language for the per-group report.
/// </summary>
internal sealed class LiveCorpusView
{
    public required string Directory { get; init; }

    public required IReadOnlyList<RetrievalEvalCorpus.FixtureDocument> Documents { get; init; }

    public required IReadOnlyList<LabeledQuery> Queries { get; init; }

    public required IReadOnlyDictionary<string, LiveQueryLabel> Labels { get; init; }

    public required RetrievalEvalLiveCorpus Source { get; init; }

    public static LiveCorpusView Load()
    {
        var corpus = RetrievalEvalLiveCorpus.Load();
        return new LiveCorpusView
        {
            Source = corpus,
            Directory = corpus.DirectoryPath,
            Documents = corpus.Documents,
            Queries = corpus.LabeledQueries,
            Labels = corpus.Queries.ToDictionary(static query => query.Labeled.Id,
                static query => new LiveQueryLabel
                {
                    Category = query.Category,
                    Language = query.Language,
                    BoundaryPhrases = query.BoundaryPhrases,
                    EnglishOnly = IsEnglishOnly(query.Language, query.Category, query.Labeled.RelevantDocumentKeys)
                },
                StringComparer.Ordinal)
        };
    }

    /// <summary>
    ///     Throws <see cref="InvalidDataException" /> unless every chunk-boundary label straddles a chunk boundary under the
    ///     live ingestion's chunking and import paths; a private corpus is otherwise never checked.
    /// </summary>
    public Task VerifyBoundaryLabelsAsync(CancellationToken cancellationToken) =>
        Source.VerifyBoundaryPhrasesSpanChunksAsync(SourcePathOf, cancellationToken);

    /// <summary>
    ///     Whether a query belongs in the EN-only slice: English, not cross-language, no relevant document under
    ///     <c>de/</c>. No English query labels a German <c>long/</c> or <c>distractors/</c> document relevant, so the
    ///     prefix is the whole rule.
    /// </summary>
    public static bool IsEnglishOnly(string language, string category, IReadOnlyList<string> relevantDocumentKeys) =>
        language == "en"
        && category != "cross-language"
        && !relevantDocumentKeys.Any(static key => key.StartsWith("de/", StringComparison.Ordinal));

    /// <summary>
    ///     The repository-relative import path: the key with the pinned-copy suffix undone (<c>code/FtsSearch.cs.txt</c>
    ///     → <c>code/FtsSearch.cs</c>), so ingestion sees the real extension and treats it as code.
    /// </summary>
    public static string SourcePathOf(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        const string pinnedCopySuffix = ".txt";
        var withoutSuffix = key.EndsWith(pinnedCopySuffix, StringComparison.Ordinal) ? key[..^pinnedCopySuffix.Length] : key;
        return Path.HasExtension(withoutSuffix) ? withoutSuffix : key;
    }
}
