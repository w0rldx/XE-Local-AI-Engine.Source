namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;

internal sealed record RetrievalCapacityProfile(string Name, int ChunkCount, int NamespaceCount, int QueryRepetitions)
{
    public int LargestNamespaceChunkCount => (int)Math.Ceiling(ChunkCount / (double)NamespaceCount);

    public static IReadOnlyDictionary<string, RetrievalCapacityProfile> All { get; } =
        new[]
        {
            new RetrievalCapacityProfile("smoke", 256, 2, 2),
            new RetrievalCapacityProfile("10k", 10_000, 4, 3),
            new RetrievalCapacityProfile("100k", 100_000, 4, 2),
            new RetrievalCapacityProfile("250k", 250_000, 4, 1),
            new RetrievalCapacityProfile("500k", 500_000, 4, 1),
            new RetrievalCapacityProfile("1m", 1_000_000, 4, 1)
        }.ToDictionary(static profile => profile.Name, StringComparer.OrdinalIgnoreCase);

    public static RetrievalCapacityProfile Parse(string name) =>
        All.TryGetValue(name, out var profile)
            ? profile
            : throw new ArgumentException($"Unknown capacity profile '{name}'. Expected one of: {string.Join(", ", All.Keys)}.", nameof(name));
}

internal sealed record RetrievalCapacityBuildMetrics(
    double SchemaMilliseconds,
    double CorpusMilliseconds,
    double FtsIndexMilliseconds,
    double VectorIndexMilliseconds,
    double TotalMilliseconds,
    long DatabaseBytes,
    long WorkingSetBaselineBytes,
    long SampledWorkingSetHighWaterBytes,
    long ManagedHeapBaselineBytes,
    long SampledManagedHeapHighWaterBytes);

internal sealed record RetrievalCapacityLatency(double P50Milliseconds, double P95Milliseconds, double MaxMilliseconds)
{
    public static RetrievalCapacityLatency From(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0)
        {
            throw new InvalidOperationException("A latency distribution cannot be computed from zero samples.");
        }

        var ordered = samples.Order().ToArray();
        return new RetrievalCapacityLatency(Percentile(ordered, 0.50d), Percentile(ordered, 0.95d), ordered[^1]);
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        var rank = Math.Max(1, (int)Math.Ceiling(percentile * ordered.Count));
        return ordered[rank - 1];
    }
}

internal sealed record RetrievalCapacityQueryMetrics(
    int QueryCount,
    int AnswerableQueryCount,
    int NoAnswerQueryCount,
    int NonEmptyAnswerableResults,
    double RecallAtK,
    double MeanReciprocalRank,
    double NdcgAtK,
    double NoAnswerFalsePositiveRate,
    RetrievalCapacityLatency Fts,
    RetrievalCapacityLatency Vector,
    RetrievalCapacityLatency Fusion,
    RetrievalCapacityLatency EndToEnd,
    bool MeetsP95Target,
    double P95TargetMilliseconds);

internal sealed record RetrievalCapacityReport(
    int Seed,
    RetrievalCapacityProfile Profile,
    RetrievalCapacityBuildMetrics Build,
    RetrievalCapacityQueryMetrics Query,
    string MeasurementMode = "warm-cache-process-only")
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string Summarize() =>
        string.Create(CultureInfo.InvariantCulture,
            $"profile={Profile.Name} seed={Seed} chunks={Profile.ChunkCount} namespaces={Profile.NamespaceCount} largestNamespaceChunks={Profile.LargestNamespaceChunkCount} "
            + $"buildMs={Build.TotalMilliseconds:F1} corpusMs={Build.CorpusMilliseconds:F1} ftsBuildMs={Build.FtsIndexMilliseconds:F1} vectorBuildMs={Build.VectorIndexMilliseconds:F1} "
            + $"measurement={MeasurementMode} dbMiB={Build.DatabaseBytes / 1048576d:F1} sampledWorkingSetHighWaterMiB={Build.SampledWorkingSetHighWaterBytes / 1048576d:F1} sampledManagedHeapHighWaterMiB={Build.SampledManagedHeapHighWaterBytes / 1048576d:F1} "
            + $"queries={Query.QueryCount} recall@5={Query.RecallAtK:F3} MRR={Query.MeanReciprocalRank:F3} nDCG@5={Query.NdcgAtK:F3} noAnswerFalsePositiveRate={Query.NoAnswerFalsePositiveRate:F3} "
            + $"ftsP50/P95/maxMs={Query.Fts.P50Milliseconds:F1}/{Query.Fts.P95Milliseconds:F1}/{Query.Fts.MaxMilliseconds:F1} "
            + $"vectorP50/P95/maxMs={Query.Vector.P50Milliseconds:F1}/{Query.Vector.P95Milliseconds:F1}/{Query.Vector.MaxMilliseconds:F1} "
            + $"fusionP50/P95/maxMs={Query.Fusion.P50Milliseconds:F3}/{Query.Fusion.P95Milliseconds:F3}/{Query.Fusion.MaxMilliseconds:F3} "
            + $"e2eP50/P95/maxMs={Query.EndToEnd.P50Milliseconds:F1}/{Query.EndToEnd.P95Milliseconds:F1}/{Query.EndToEnd.MaxMilliseconds:F1} "
            + $"p95TargetMs={Query.P95TargetMilliseconds:F0} targetMet={Query.MeetsP95Target}");

    public void WriteJson(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}

internal static class RetrievalCapacityBenchmark
{
    public const int Seed = 0x5EED_2026;
    public const int K = 5;
    public const int VectorDimensions = KnowledgeEmbeddingVectorPolicy.MatryoshkaWidth;
    public const string EmbeddingModel = "capacity-deterministic-v1";
    public const string VectorIdentity = "capacity:fixed-seed:v1:512";

    private const int ScenarioCount = 4;
    private const int InsertBatchSize = 10_000;

    public static async Task<RetrievalCapacityReport> RunAsync(string databasePath,
        INodeSqliteKeyHolder keyHolder,
        RetrievalCapacityProfile profile,
        double p95TargetMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(keyHolder);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfLessThan(profile.ChunkCount, profile.NamespaceCount * ScenarioCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(profile.NamespaceCount, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(profile.QueryRepetitions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(p95TargetMilliseconds, 0d);

        var memory = new MemorySampler();
        var totalStarted = Stopwatch.GetTimestamp();
        var schemaStarted = Stopwatch.GetTimestamp();
        await using (var migrationContext = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, keyHolder))
        {
            await migrationContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        var schemaMilliseconds = Stopwatch.GetElapsedTime(schemaStarted).TotalMilliseconds;
        memory.Sample();

        IReadOnlyList<CapacityQuery> queries;
        double corpusMilliseconds;
        double ftsIndexMilliseconds;
        double vectorIndexMilliseconds;
        await using (var buildContext = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, keyHolder))
        {
            var connection = buildContext.Database.GetDbConnection();
            await OpenAsync(connection, cancellationToken).ConfigureAwait(false);
            await DisableFtsMaintenanceAsync(connection, cancellationToken).ConfigureAwait(false);

            (queries, corpusMilliseconds) = await InsertCorpusAsync(buildContext, connection, profile, memory, cancellationToken).ConfigureAwait(false);

            var ftsStarted = Stopwatch.GetTimestamp();
            await ExecuteAsync(connection, "INSERT INTO chunk_fts(chunk_fts) VALUES ('rebuild');", cancellationToken).ConfigureAwait(false);
            ftsIndexMilliseconds = Stopwatch.GetElapsedTime(ftsStarted).TotalMilliseconds;
            memory.Sample();

            var vectorStarted = Stopwatch.GetTimestamp();
            await InsertVectorsAsync(connection, profile, memory, cancellationToken).ConfigureAwait(false);
            vectorIndexMilliseconds = Stopwatch.GetElapsedTime(vectorStarted).TotalMilliseconds;
            memory.Sample();

            var chunkRows = await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_document_chunks;", cancellationToken).ConfigureAwait(false);
            var ftsRows = await CountAsync(connection, "SELECT COUNT(*) FROM chunk_fts;", cancellationToken).ConfigureAwait(false);
            var vectorRows = await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_chunk_vectors;", cancellationToken).ConfigureAwait(false);
            if (chunkRows != profile.ChunkCount || ftsRows != profile.ChunkCount || vectorRows != profile.ChunkCount)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"Capacity profile '{profile.Name}' declared {profile.ChunkCount} chunks but built chunks={chunkRows}, fts={ftsRows}, vectors={vectorRows}."));
            }
        }

        var totalMilliseconds = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
        var databaseBytes = DatabaseFootprint(databasePath);
        var queryMetrics = await MeasureQueriesAsync(databasePath,
            keyHolder,
            profile,
            queries,
            p95TargetMilliseconds,
            memory,
            cancellationToken).ConfigureAwait(false);
        memory.Sample();

        return new RetrievalCapacityReport(Seed,
            profile,
            new RetrievalCapacityBuildMetrics(schemaMilliseconds,
                corpusMilliseconds,
                ftsIndexMilliseconds,
                vectorIndexMilliseconds,
                totalMilliseconds,
                databaseBytes,
                memory.WorkingSetBaselineBytes,
                memory.SampledWorkingSetHighWaterBytes,
                memory.ManagedHeapBaselineBytes,
                memory.SampledManagedHeapHighWaterBytes),
            queryMetrics);
    }

    internal static void RefuseVacuousQueryRun(int queryCount, int answerableQueryCount, int nonEmptyAnswerableResults)
    {
        if (queryCount == 0)
        {
            throw new InvalidOperationException("Capacity benchmark refused a zero-query run.");
        }

        if (answerableQueryCount == 0)
        {
            throw new InvalidOperationException("Capacity benchmark refused a run with no answerable queries.");
        }

        if (nonEmptyAnswerableResults == 0)
        {
            throw new InvalidOperationException("Capacity benchmark refused a zero-result answerable run.");
        }
    }

    private static async Task<(IReadOnlyList<CapacityQuery> Queries, double ElapsedMilliseconds)> InsertCorpusAsync(NodeChatDbContext context,
        DbConnection connection,
        RetrievalCapacityProfile profile,
        MemorySampler memory,
        CancellationToken cancellationToken)
    {
        var documentIds = Enumerable.Range(0, profile.NamespaceCount)
                                    .Select(index => StableGuid("document", index))
                                    .ToArray();
        var queries = BuildQueries(profile);
        var started = Stopwatch.GetTimestamp();

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var documentCommand = connection.CreateCommand();
            documentCommand.Transaction = transaction;
            documentCommand.CommandText =
                """
                INSERT INTO knowledge_documents
                    (document_id, collection_id, original_file_name, mime_type, extension, size_bytes, content_hash,
                     storage_path, source_kind, status, chunk_count, embedding_model, vector_identity, vector_dim,
                     parser_version, chunker_version, created_at_utc, updated_at_utc)
                VALUES
                    ($document_id, $collection_id, $name, 'text/plain', '.txt', 1, $content_hash,
                     $storage_path, 'upload', 'Indexed', $chunk_count, $embedding_model, $vector_identity, $vector_dim,
                     'capacity-v1', 'capacity-v1', 1, 1);
                """;
            var documentParameters = AddParameters(documentCommand,
                "$document_id", "$collection_id", "$name", "$content_hash", "$storage_path", "$chunk_count",
                "$embedding_model", "$vector_identity", "$vector_dim");

            for (var namespaceIndex = 0; namespaceIndex < profile.NamespaceCount; namespaceIndex++)
            {
                var collectionId = GetCollectionId(namespaceIndex);
                var documentId = documentIds[namespaceIndex];
                documentParameters[0].Value = documentId;
                documentParameters[1].Value = collectionId;
                documentParameters[2].Value = context.EncryptKnowledgeFileName($"capacity-{namespaceIndex}.txt", documentId);
                documentParameters[3].Value = $"capacity-{Seed}-{namespaceIndex}";
                documentParameters[4].Value = $"capacity-{namespaceIndex}.txt";
                documentParameters[5].Value = ChunkCountForNamespace(profile, namespaceIndex);
                documentParameters[6].Value = EmbeddingModel;
                documentParameters[7].Value = VectorIdentity;
                documentParameters[8].Value = VectorDimensions;
                _ = await documentCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var chunkCommand = connection.CreateCommand();
            chunkCommand.Transaction = transaction;
            chunkCommand.CommandText =
                """
                INSERT INTO knowledge_document_chunks
                    (chunk_id, document_id, chunk_index, content, token_count, heading_path, start_offset, end_offset,
                     content_kind, source_path, language, symbol, content_hash, embedding_input_hash)
                VALUES
                    ($chunk_id, $document_id, $chunk_index, $content, $token_count, $heading_path, 0, $end_offset,
                     $content_kind, $source_path, $language, $symbol, $content_hash, $embedding_input_hash);
                """;
            var chunkParameters = AddParameters(chunkCommand,
                "$chunk_id", "$document_id", "$chunk_index", "$content", "$token_count", "$heading_path",
                "$end_offset", "$content_kind", "$source_path", "$language", "$symbol", "$content_hash",
                "$embedding_input_hash");

            var globalIndex = 0;
            for (var namespaceIndex = 0; namespaceIndex < profile.NamespaceCount; namespaceIndex++)
            {
                var count = ChunkCountForNamespace(profile, namespaceIndex);
                for (var localIndex = 0; localIndex < count; localIndex++, globalIndex++)
                {
                    var row = CorpusRow(namespaceIndex, localIndex, globalIndex);
                    chunkParameters[0].Value = row.ChunkId;
                    chunkParameters[1].Value = documentIds[namespaceIndex];
                    chunkParameters[2].Value = localIndex;
                    chunkParameters[3].Value = row.Content;
                    chunkParameters[4].Value = row.TokenCount;
                    chunkParameters[5].Value = row.HeadingPath;
                    chunkParameters[6].Value = row.Content.Length;
                    chunkParameters[7].Value = row.ContentKind;
                    chunkParameters[8].Value = row.SourcePath;
                    chunkParameters[9].Value = row.Language;
                    chunkParameters[10].Value = (object?)row.Symbol ?? DBNull.Value;
                    chunkParameters[11].Value = $"content-{globalIndex:X8}";
                    chunkParameters[12].Value = $"embedding-{globalIndex:X8}";
                    _ = await chunkCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    if ((globalIndex + 1) % InsertBatchSize == 0)
                    {
                        memory.Sample();
                    }
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return (queries, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static async Task InsertVectorsAsync(DbConnection connection,
        RetrievalCapacityProfile profile,
        MemorySampler memory,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO knowledge_chunk_vectors
                (chunk_id, document_id, dim, embedding, embedding_model, vector_identity)
            VALUES ($chunk_id, $document_id, $dim, $embedding, $embedding_model, $vector_identity);
            """;
        var parameters = AddParameters(command, "$chunk_id", "$document_id", "$dim", "$embedding", "$embedding_model", "$vector_identity");
        parameters[2].Value = VectorDimensions;
        parameters[4].Value = EmbeddingModel;
        parameters[5].Value = VectorIdentity;
        var vectorBytes = Enumerable.Range(0, VectorDimensions).Select(VectorBytes).ToArray();

        var globalIndex = 0;
        for (var namespaceIndex = 0; namespaceIndex < profile.NamespaceCount; namespaceIndex++)
        {
            var documentId = StableGuid("document", namespaceIndex);
            var count = ChunkCountForNamespace(profile, namespaceIndex);
            for (var localIndex = 0; localIndex < count; localIndex++, globalIndex++)
            {
                parameters[0].Value = StableGuid("chunk", globalIndex);
                parameters[1].Value = documentId;
                parameters[3].Value = vectorBytes[VectorDimensionFor(localIndex, globalIndex)];
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if ((globalIndex + 1) % InsertBatchSize == 0)
                {
                    memory.Sample();
                }
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RetrievalCapacityQueryMetrics> MeasureQueriesAsync(string databasePath,
        INodeSqliteKeyHolder keyHolder,
        RetrievalCapacityProfile profile,
        IReadOnlyList<CapacityQuery> queries,
        double p95TargetMilliseconds,
        MemorySampler memory,
        CancellationToken cancellationToken)
    {
        if (queries.Count == 0)
        {
            RefuseVacuousQueryRun(0, 0, 0);
        }

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, keyHolder);
        var connection = context.Database.GetDbConnection();
        await OpenAsync(connection, cancellationToken).ConfigureAwait(false);
        var ftsSearch = new FtsSearch(context);
        var vectorSearch = new ManagedCosineVectorSearch(context, new CompletedNormalizationState());
        var fusion = new ReciprocalRankFusion();
        var ftsSamples = new List<double>();
        var vectorSamples = new List<double>();
        var fusionSamples = new List<double>();
        var endToEndSamples = new List<double>();
        var evaluations = new List<CapacityEvaluation>();

        // One unmeasured warm-up per query shape pays JIT and first-page costs. Repeating the same five shapes for every
        // namespace would add large exact-vector scans without improving warm-up quality at the 500k/1M profiles.
        foreach (var query in queries.Take(ScenarioCount + 1))
        {
            _ = await ExecuteQueryAsync(connection, ftsSearch, vectorSearch, fusion, query, cancellationToken).ConfigureAwait(false);
        }

        for (var repetition = 0; repetition < profile.QueryRepetitions; repetition++)
        {
            foreach (var query in queries)
            {
                var result = await ExecuteQueryAsync(connection, ftsSearch, vectorSearch, fusion, query, cancellationToken).ConfigureAwait(false);
                ftsSamples.Add(result.FtsMilliseconds);
                vectorSamples.Add(result.VectorMilliseconds);
                fusionSamples.Add(result.FusionMilliseconds);
                endToEndSamples.Add(result.EndToEndMilliseconds);
                if (repetition == 0)
                {
                    evaluations.Add(Evaluate(query, result));
                }

                memory.Sample();
            }
        }

        var answerable = evaluations.Where(static evaluation => !evaluation.ExpectsNoAnswer).ToList();
        var noAnswer = evaluations.Where(static evaluation => evaluation.ExpectsNoAnswer).ToList();
        var nonEmptyAnswerableResults = answerable.Count(static evaluation => evaluation.ResultCount > 0);
        RefuseVacuousQueryRun(evaluations.Count, answerable.Count, nonEmptyAnswerableResults);

        var endToEnd = RetrievalCapacityLatency.From(endToEndSamples);
        return new RetrievalCapacityQueryMetrics(evaluations.Count,
            answerable.Count,
            noAnswer.Count,
            nonEmptyAnswerableResults,
            answerable.Average(static evaluation => evaluation.RelevantRetrieved ? 1d : 0d),
            answerable.Average(static evaluation => evaluation.ReciprocalRank),
            answerable.Average(static evaluation => evaluation.NdcgAtK),
            noAnswer.Average(static evaluation => evaluation.ResultCount > 0 ? 1d : 0d),
            RetrievalCapacityLatency.From(ftsSamples),
            RetrievalCapacityLatency.From(vectorSamples),
            RetrievalCapacityLatency.From(fusionSamples),
            endToEnd,
            endToEnd.P95Milliseconds <= p95TargetMilliseconds,
            p95TargetMilliseconds);
    }

    private static async Task<CapacityQueryResult> ExecuteQueryAsync(DbConnection connection,
        IFtsSearch ftsSearch,
        IVectorSearch vectorSearch,
        IRankingFusionService fusion,
        CapacityQuery query,
        CancellationToken cancellationToken)
    {
        var endToEndStarted = Stopwatch.GetTimestamp();
        var ftsStarted = Stopwatch.GetTimestamp();
        var ftsHits = await ftsSearch.SearchAsync(query.Text, K * 4, null, query.CollectionId, cancellationToken).ConfigureAwait(false);
        var ftsMilliseconds = Stopwatch.GetElapsedTime(ftsStarted).TotalMilliseconds;

        IReadOnlyList<VectorSearchHit> vectorHits = [];
        var vectorStarted = Stopwatch.GetTimestamp();
        if (query.VectorDimension is int vectorDimension)
        {
            vectorHits = await vectorSearch.SearchAsync(UnitVector(vectorDimension),
                                               EmbeddingModel,
                                               VectorIdentity,
                                               VectorDimensions,
                                               K * 4,
                                               null,
                                               query.CollectionId,
                                               cancellationToken)
                                           .ConfigureAwait(false);
        }

        var vectorMilliseconds = Stopwatch.GetElapsedTime(vectorStarted).TotalMilliseconds;
        var fusionStarted = Stopwatch.GetTimestamp();
        var fused = fusion.FuseScored([
                ftsHits.Select(static hit => new RankFusionInput(hit.ChunkId, -hit.Bm25Score)).ToList(),
                vectorHits.Select(static hit => new RankFusionInput(hit.ChunkId, hit.Score)).ToList()
            ],
            RankFusionStrategy.ScoreAware,
            scoreWeight: 1d);
        var selected = fused.Take(K).Select(static hit => hit.ChunkId).ToList();
        var fusionMilliseconds = Stopwatch.GetElapsedTime(fusionStarted).TotalMilliseconds;
        var hydrated = await HydrateAsync(connection, selected, query.CollectionId, cancellationToken).ConfigureAwait(false);
        var endToEndMilliseconds = Stopwatch.GetElapsedTime(endToEndStarted).TotalMilliseconds;
        return new CapacityQueryResult(hydrated,
            selected.Count,
            ftsMilliseconds,
            vectorMilliseconds,
            fusionMilliseconds,
            endToEndMilliseconds);
    }

    private static async Task<IReadOnlyList<Guid>> HydrateAsync(DbConnection connection,
        IReadOnlyList<Guid> chunkIds,
        string collectionId,
        CancellationToken cancellationToken)
    {
        if (chunkIds.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        var placeholders = new string[chunkIds.Count];
        for (var index = 0; index < chunkIds.Count; index++)
        {
            placeholders[index] = $"$id{index}";
            var parameter = command.CreateParameter();
            parameter.ParameterName = placeholders[index];
            parameter.Value = chunkIds[index];
            _ = command.Parameters.Add(parameter);
        }

        var collectionParameter = command.CreateParameter();
        collectionParameter.ParameterName = "$collection_id";
        collectionParameter.Value = collectionId;
        _ = command.Parameters.Add(collectionParameter);
#pragma warning disable CA2100 // Placeholder names are generated locally; every value remains a DbParameter.
        command.CommandText =
            $"SELECT c.chunk_id FROM knowledge_document_chunks c JOIN knowledge_documents d ON d.document_id = c.document_id WHERE c.chunk_id IN ({string.Join(',', placeholders)}) AND d.collection_id = $collection_id;";
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var present = new HashSet<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = present.Add(Guid.Parse(reader.GetString(0)));
        }

        return chunkIds.Where(present.Contains).ToList();
    }

    private static CapacityEvaluation Evaluate(CapacityQuery query, CapacityQueryResult result)
    {
        if (result.SelectedCount != result.ChunkIds.Count)
        {
            throw new InvalidOperationException($"Query '{query.Id}' selected a chunk outside collection '{query.CollectionId}'.");
        }

        if (query.ExpectsNoAnswer)
        {
            return new CapacityEvaluation(true, false, 0d, 0d, result.ChunkIds.Count);
        }

        var rank = 0;
        for (var index = 0; index < result.ChunkIds.Count; index++)
        {
            if (result.ChunkIds[index] == query.RelevantChunkId)
            {
                rank = index + 1;
                break;
            }
        }

        return rank == 0
            ? new CapacityEvaluation(false, false, 0d, 0d, result.ChunkIds.Count)
            : new CapacityEvaluation(false, true, 1d / rank, 1d / Math.Log2(rank + 1d), result.ChunkIds.Count);
    }

    private static IReadOnlyList<CapacityQuery> BuildQueries(RetrievalCapacityProfile profile)
    {
        var queries = new List<CapacityQuery>(profile.NamespaceCount * (ScenarioCount + 1));
        for (var namespaceIndex = 0; namespaceIndex < profile.NamespaceCount; namespaceIndex++)
        {
            var baseGlobalIndex = Enumerable.Range(0, namespaceIndex).Sum(index => ChunkCountForNamespace(profile, index));
            var collectionId = GetCollectionId(namespaceIndex);
            queries.Add(new CapacityQuery($"ns{namespaceIndex}-english", "quartz retention seven31", collectionId, StableGuid("chunk", baseGlobalIndex), 0, false));
            queries.Add(new CapacityQuery($"ns{namespaceIndex}-german", "aufbewahrung kupfer sieben31", collectionId, StableGuid("chunk", baseGlobalIndex + 1), 1, false));
            queries.Add(new CapacityQuery($"ns{namespaceIndex}-code", "ResolveTenantToken src auth tenantresolver cs", collectionId, StableGuid("chunk", baseGlobalIndex + 2), 2, false));
            queries.Add(new CapacityQuery($"ns{namespaceIndex}-distractor", "cobalt orchid beacon", collectionId, StableGuid("chunk", baseGlobalIndex + 3), 3, false));
            queries.Add(new CapacityQuery($"ns{namespaceIndex}-no-answer", "zephyr nonexistent axiom", collectionId, Guid.Empty, VectorDimensions - 1, true));
        }

        return queries;
    }

    private static CapacityCorpusRow CorpusRow(int namespaceIndex, int localIndex, int globalIndex)
    {
        return localIndex switch
        {
            0 => new CapacityCorpusRow(StableGuid("chunk", globalIndex), "English policy: quartz retention seven31 days authoritative schedule.", 7, "Policy > Retention", "text",
                "policies/retention-en.md", "en", null),
            1 => new CapacityCorpusRow(StableGuid("chunk", globalIndex), "Deutsche Richtlinie: aufbewahrung kupfer sieben31 Tage verbindlich.", 7, "Richtlinie > Aufbewahrung", "text",
                "richtlinien/aufbewahrung-de.md", "de", null),
            2 => new CapacityCorpusRow(StableGuid("chunk", globalIndex), "internal string ResolveTenantToken() validates tenant scope before issuing a token.", 10, "TenantResolver", "code",
                "src/auth/TenantResolver.cs", "csharp", "ResolveTenantToken"),
            3 => new CapacityCorpusRow(StableGuid("chunk", globalIndex), "Operational signal cobalt orchid beacon identifies the canonical recovery record.", 9, "Operations > Recovery", "text",
                "runbooks/recovery.md", "en", null),
            _ => DistractorRow(namespaceIndex, localIndex, globalIndex)
        };
    }

    private static CapacityCorpusRow DistractorRow(int namespaceIndex, int localIndex, int globalIndex)
    {
        var state = unchecked((uint)(Seed + (globalIndex * 747796405)));
        var first = DistractorToken(Next(ref state));
        var second = DistractorToken(Next(ref state));
        var third = DistractorToken(Next(ref state));
        var content = string.Create(CultureInfo.InvariantCulture,
            $"Synthetic distractor namespace {namespaceIndex} item {localIndex}: {first} {second} {third} routine handbook material.");
        return new CapacityCorpusRow(StableGuid("chunk", globalIndex), content, 10, "Synthetic > Distractor", "text", $"synthetic/{localIndex:D8}.md", "en", null);
    }

    private static string DistractorToken(uint value)
    {
        string[] tokens = ["amber", "birch", "delta", "ember", "fjord", "granite", "harbor", "indigo", "juniper", "kernel", "lattice", "meadow", "nickel", "opal", "prairie", "raven"];
        return tokens[value % (uint)tokens.Length];
    }

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static int VectorDimensionFor(int localIndex, int globalIndex) =>
        localIndex < ScenarioCount ? localIndex : 4 + (int)(RetrievalTokens.Fnv1a(globalIndex.ToString(CultureInfo.InvariantCulture)) % (VectorDimensions - 4));

    private static byte[] VectorBytes(int dimension)
    {
        var vector = UnitVector(dimension);
        return MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
    }

    private static float[] UnitVector(int dimension)
    {
        var vector = new float[VectorDimensions];
        vector[dimension] = 1f;
        return vector;
    }

    private static Guid StableGuid(string domain, int index)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Seed}:{domain}:{index}")));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static int ChunkCountForNamespace(RetrievalCapacityProfile profile, int namespaceIndex) =>
        (profile.ChunkCount / profile.NamespaceCount) + (namespaceIndex < profile.ChunkCount % profile.NamespaceCount ? 1 : 0);

    private static string GetCollectionId(int namespaceIndex) =>
        $"CAPACITY-{namespaceIndex:D2}";

    private static async Task DisableFtsMaintenanceAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await ExecuteAsync(connection,
            "DROP TRIGGER IF EXISTS knowledge_document_chunks_au; DROP TRIGGER IF EXISTS knowledge_document_chunks_ad; DROP TRIGGER IF EXISTS knowledge_document_chunks_ai;",
            cancellationToken).ConfigureAwait(false);

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Callers are internal benchmark helpers and pass only fixed SQL literals.
        command.CommandText = sql;
#pragma warning restore CA2100
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> CountAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Callers are internal benchmark helpers and pass only fixed SQL literals.
        command.CommandText = sql;
#pragma warning restore CA2100
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task OpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static DbParameter[] AddParameters(DbCommand command, params string[] names)
    {
        var parameters = new DbParameter[names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = names[index];
            _ = command.Parameters.Add(parameter);
            parameters[index] = parameter;
        }

        return parameters;
    }

    private static long DatabaseFootprint(string databasePath)
    {
        return new[]
               {
                   databasePath,
                   databasePath + "-wal",
                   databasePath + "-shm"
               }
               .Where(File.Exists)
               .Sum(path => new FileInfo(path).Length);
    }

    private sealed class MemorySampler
    {
        public MemorySampler()
        {
            using var process = Process.GetCurrentProcess();
            WorkingSetBaselineBytes = process.WorkingSet64;
            ManagedHeapBaselineBytes = GC.GetTotalMemory(forceFullCollection: false);
            SampledWorkingSetHighWaterBytes = WorkingSetBaselineBytes;
            SampledManagedHeapHighWaterBytes = ManagedHeapBaselineBytes;
        }

        public long WorkingSetBaselineBytes { get; }

        public long SampledWorkingSetHighWaterBytes { get; private set; }

        public long ManagedHeapBaselineBytes { get; }

        public long SampledManagedHeapHighWaterBytes { get; private set; }

        public void Sample()
        {
            using var process = Process.GetCurrentProcess();
            SampledWorkingSetHighWaterBytes = Math.Max(SampledWorkingSetHighWaterBytes, process.WorkingSet64);
            SampledManagedHeapHighWaterBytes = Math.Max(SampledManagedHeapHighWaterBytes, GC.GetTotalMemory(forceFullCollection: false));
        }
    }

    private sealed class CompletedNormalizationState : IKnowledgeVectorNormalizationState
    {
        public bool IsComplete => true;

        public void MarkComplete()
        {
        }
    }

    private sealed record CapacityQuery(
        string Id,
        string Text,
        string CollectionId,
        Guid RelevantChunkId,
        int? VectorDimension,
        bool ExpectsNoAnswer);

    private sealed record CapacityCorpusRow(
        Guid ChunkId,
        string Content,
        int TokenCount,
        string HeadingPath,
        string ContentKind,
        string SourcePath,
        string Language,
        string? Symbol);

    private sealed record CapacityQueryResult(
        IReadOnlyList<Guid> ChunkIds,
        int SelectedCount,
        double FtsMilliseconds,
        double VectorMilliseconds,
        double FusionMilliseconds,
        double EndToEndMilliseconds);

    private sealed record CapacityEvaluation(
        bool ExpectsNoAnswer,
        bool RelevantRetrieved,
        double ReciprocalRank,
        double NdcgAtK,
        int ResultCount);
}
