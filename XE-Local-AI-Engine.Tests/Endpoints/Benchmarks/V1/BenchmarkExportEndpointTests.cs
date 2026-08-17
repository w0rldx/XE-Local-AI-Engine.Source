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
        AssertEx.Equal(expected: 1, root.GetProperty("schemaVersion").GetInt32());
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
                IsWarmup = false
            });
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, Api + $"/projects/{ProjectId}/export.csv");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var lines = body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        AssertEx.Equal(expected: 2, lines.Length, "One header row and one run row.");
        AssertEx.Equal("rank,modelGroupKey,model,quant,kvCacheType,flashAttention,backend,placement,contextTokens,status,stopReason,"
                       + "repeatGroupId,repeatIndex,isWarmup,totalTokens,tokensPerSecond,ttftMs,promptTokens,promptTokensPerSecond,"
                       + "generationTokens,generationTokensPerSecond,cachedPromptTokens,segmentCount,durationMs,qualityScore,qualityScoreSource,"
                       + "judgeScore,userScore,rankExclusionReason,launchIdentity,receiptHash",
            lines[0]);

        // pp is 123 tokens over 500 ms = 246, tg is 89 over 2000 ms = 44.5. The group key is the base model, so the
        // quant is dropped from it and kept in its own column.
        AssertEx.Equal("1,\"owner/My,Model-GGUF\",\"owner/My,Model-GGUF:Q4_K_M\",Q4_K_M,,,,,4096,succeeded,,"
                       + "50000000-0000-0000-0000-000000000005,2,false,512,41.5,180.25,123,246,89,44.5,7,2,12340,73,judge,,,,,",
            lines[1]);
    }

    private static void ArrangeProject(Context context) =>
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Project());

    private static void ArrangeRuns(Context context, params BenchmarkRunRecord[] runs)
    {
        context.Store.ListRunsAsync(ProjectId, 0, 200, null, true, Arg.Any<CancellationToken>())
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

    private static BenchmarkRunRecord Run(BenchmarkPrimaryStatus primary = BenchmarkPrimaryStatus.Queued,
        string? output = null,
        Guid? judgeAttemptId = null,
        string modelName = "model") =>
        new(RunId,
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

        public TestServerWebAppFactory Factory { get; }

        public Context() =>
            Factory = new TestServerWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<IBenchmarkStore>();
                    services.AddSingleton(Store);
                }
            };

        public ValueTask DisposeAsync() =>
            Factory.DisposeAsync();
    }
}
