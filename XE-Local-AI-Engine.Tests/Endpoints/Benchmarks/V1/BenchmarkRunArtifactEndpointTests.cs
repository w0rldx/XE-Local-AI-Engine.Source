namespace XE_Local_AI_Engine.Tests.Endpoints.Benchmarks.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;
using XE_Local_AI_Engine.Client.Testing.Fakes;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The artifact download and the delete cleanup over the real blob store on a scratch directory and a substituted store: the bytes, the headers
///     and the integrity refusal are the host's, and only a real ciphertext on disk can be tampered with.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class BenchmarkRunArtifactEndpointTests
{
    private const string Api = "/api/local/v1/benchmarks";
    private static readonly Guid ProjectId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid RunId = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid ArtifactId = new("00000000-0000-0000-0000-000000000004");
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("<script>alert(1)</script>\nFAILED test_one ✗\n");

    [Test]
    public async Task Get_WhenNotOperator_Returns403()
    {
        await using var context = new Context();
        using var client = context.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ArtifactRoute());
        context.Factory.AddNonOperatorBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _ = context.Store.DidNotReceive().GetRunArtifactsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Get_ReturnsAttachmentWithNosniff()
    {
        await using var context = new Context();
        await context.ArrangeArtifactAsync();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, ArtifactRoute());

        using var response = await client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, Encoding.UTF8.GetString(bytes));
        AssertEx.True(bytes.AsSpan().SequenceEqual(Content), "The exact stored bytes, untouched.");
        AssertEx.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        AssertEx.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        AssertEx.True(response.Headers.TryGetValues("X-Content-Type-Options", out var sniff), "nosniff must be sent.");
        AssertEx.Equal("nosniff", AssertEx.NotNull(sniff).Single());
    }

    [Test]
    public async Task Get_WhenTampered_Returns409()
    {
        await using var context = new Context();
        await context.ArrangeArtifactAsync();
        var path = context.BlobPath(RunId, ArtifactId);
        var onDisk = await File.ReadAllBytesAsync(path);
        onDisk[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, onDisk);
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, ArtifactRoute());

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(nameof(BenchmarkErrorCode.ArtifactCorrupt), document.RootElement.GetProperty("code").GetString());
        AssertEx.False(body.Contains("FAILED test_one", StringComparison.Ordinal), "Corrupt bytes are never served.");
    }

    [Test]
    public async Task Get_WhenTheBlobIsGone_Returns404ArtifactMissing()
    {
        await using var context = new Context();
        await context.ArrangeArtifactAsync();
        File.Delete(context.BlobPath(RunId, ArtifactId));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, ArtifactRoute());

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(nameof(BenchmarkErrorCode.ArtifactMissing), document.RootElement.GetProperty("code").GetString());
    }

    [Test]
    public async Task Get_WhenUnknownArtifact_Returns404()
    {
        await using var context = new Context();
        await context.ArrangeArtifactAsync();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/runs/{RunId}/artifacts/{Guid.NewGuid()}");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(nameof(BenchmarkErrorCode.NotFound), document.RootElement.GetProperty("code").GetString());
    }

    [Test]
    public async Task GetRun_ListsOnlyTheCurrentJudgeAttemptsArtifacts()
    {
        var currentAttemptId = Guid.NewGuid();
        var current = Artifact(Guid.NewGuid(), currentAttemptId);
        await using var context = new Context();
        context.Store.GetRunAsync(RunId, Arg.Any<CancellationToken>()).Returns(Run() with
        {
            Judge = Judge(currentAttemptId),
            Artifacts = [Artifact(Guid.NewGuid(), Guid.NewGuid()), current]
        });
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/runs/{RunId}");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        var artifacts = document.RootElement.GetProperty("artifacts");
        AssertEx.Equal(expected: 1, artifacts.GetArrayLength(), "An earlier attempt's artifact is not part of the current judging.");
        AssertEx.Equal(current.Id.ToString("D"), artifacts[0].GetProperty("id").GetString());
    }

    [Test]
    public async Task GetRun_WithoutAJudgeAttempt_ListsNoArtifacts()
    {
        await using var context = new Context();
        context.Store.GetRunAsync(RunId, Arg.Any<CancellationToken>()).Returns(Run() with
        {
            Artifacts = [Artifact(Guid.NewGuid(), Guid.NewGuid())]
        });
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/runs/{RunId}");

        using var response = await client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("artifacts").GetArrayLength());
    }

    [Test]
    public async Task DeleteRun_RemovesTheBlobDirectory()
    {
        await using var context = new Context();
        await context.ArrangeArtifactAsync();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Delete, Api + $"/runs/{RunId}", new
        {
            expectedVersion = 3
        });

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        _ = context.Store.Received(1).DeleteRunAsync(RunId, 3, Arg.Any<CancellationToken>());
        AssertEx.False(Directory.Exists(Path.GetDirectoryName(context.BlobPath(RunId, ArtifactId))), "The run's whole artifact directory goes with its row.");
    }

    [Test]
    public async Task DeleteRun_WhenTheStoreRefuses_KeepsTheBlobDirectory()
    {
        await using var context = new Context();
        await context.ArrangeArtifactAsync();
        context.Store.DeleteRunAsync(RunId, 3, Arg.Any<CancellationToken>())
               .Returns<Task>(_ => throw new BenchmarkConflictException("VersionConflict"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Delete, Api + $"/runs/{RunId}", new
        {
            expectedVersion = 3
        });

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.True(File.Exists(context.BlobPath(RunId, ArtifactId)), "Bytes go only after the rows did.");
    }

    [Test]
    public async Task DeleteProject_RemovesEveryRunsBlobDirectory()
    {
        await using var context = new Context();
        await context.ArrangeArtifactAsync();
        context.Store.ListAllRunsAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns(new BenchmarkRunPage
               {
                   Items = [Run()],
                   TotalCount = 1,
                   RankCohort = new BenchmarkRankCohort
                   {
                       PolicyRevision = null,
                       ExecutionKey = null,
                       CohortGeneration = null,
                       RankedCount = 0,
                       TotalScored = 0
                   }
               });
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Delete, Api + $"/projects/{ProjectId}", new
        {
            expectedVersion = 4
        });

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        _ = context.Store.Received(1).DeleteProjectAsync(ProjectId, 4, Arg.Any<CancellationToken>());
        AssertEx.False(Directory.Exists(Path.GetDirectoryName(context.BlobPath(RunId, ArtifactId))));
    }

    private static string ArtifactRoute() =>
        Api + $"/runs/{RunId}/artifacts/{ArtifactId}";

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, HttpMethod method, string path, object? content = null)
    {
        var request = new HttpRequestMessage(method, path);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        if (content is not null)
        {
            request.Content = JsonContent.Create(content);
        }

        return request;
    }

    private static BenchmarkRunArtifactRecord Artifact(Guid id, Guid judgeAttemptId) =>
        new()
        {
            Id = id,
            RunId = RunId,
            JudgeAttemptId = judgeAttemptId,
            CriterionId = "tests",
            Kind = BenchmarkRunArtifactKinds.PythonTestsStdout,
            MediaType = "text/plain; charset=utf-8",
            ByteCount = 12,
            ContentHash = "abc123",
            CreatedAtUtc = 30
        };

    private static BenchmarkRunJudgeView Judge(Guid attemptId) =>
        new()
        {
            State = BenchmarkRunJudgeStates.Succeeded,
            AttemptId = attemptId,
            Score = 60,
            PolicyRevision = 1,
            PolicyRevisionId = Guid.NewGuid(),
            AttemptSequence = 1,
            CohortGeneration = 1,
            ExecutionKey = null,
            ErrorMessage = null,
            PolicyCurrent = true,
            ExecutionCurrent = false,
            RankExclusionReason = null
        };

    private static BenchmarkRunRecord Run() =>
        new()
        {
            Id = RunId,
            ProjectId = ProjectId,
            RuntimeSnapshotJson = Encoding.UTF8.GetBytes("secret-runtime"),
            PrimaryModelName = "model",
            PrimaryModelOrigin = null,
            ModelContentFingerprint = "v1:aggregate",
            AgentName = "Agent",
            AgentVersion = 2,
            RequestedContextTokens = 4096,
            PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
            EffectiveContextTokens = null,
            DurationMs = null,
            TotalTokens = null,
            TokensPerSecond = null,
            OutputPartsJson = null,
            LastStreamSequence = 0,
            UserScore = null,
            PrimaryErrorMessage = null,
            Version = 3,
            CreatedAtUtc = 10,
            StartedAtUtc = null,
            PrimaryCompletedAtUtc = 40,
            UpdatedAtUtc = 20
        };

    private sealed class Context : IAsyncDisposable
    {
        private readonly NullNodeSqliteKeyHolder _keyHolder = new();
        private readonly TempDirectory _root = new("xe-benchmark-artifact-endpoint");

        public Context()
        {
            Blobs = new BenchmarkRunArtifactBlobStore(new FakeNodeDataDirectory(_root.Path), _keyHolder);
            Factory = new TestServerWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<IBenchmarkStore>();
                    services.AddSingleton(Store);
                    services.RemoveAll<BenchmarkRunArtifactBlobStore>();
                    services.AddSingleton(Blobs);
                }
            };
        }

        public IBenchmarkStore Store { get; } = Substitute.For<IBenchmarkStore>();

        public BenchmarkRunArtifactBlobStore Blobs { get; }

        public TestServerWebAppFactory Factory { get; }

        public async Task ArrangeArtifactAsync()
        {
            var hash = await Blobs.WriteAsync(RunId, ArtifactId, Content);
            Store.GetRunArtifactsAsync(RunId, Arg.Any<CancellationToken>())
                 .Returns(
                 [
                     new BenchmarkRunArtifactRecord
                     {
                         Id = ArtifactId,
                         RunId = RunId,
                         JudgeAttemptId = Guid.NewGuid(),
                         CriterionId = "tests",
                         Kind = BenchmarkRunArtifactKinds.PythonTestsStdout,
                         MediaType = "text/plain; charset=utf-8",
                         ByteCount = Content.Length,
                         ContentHash = hash,
                         CreatedAtUtc = 30
                     }
                 ]);
        }

        public string BlobPath(Guid runId, Guid artifactId) =>
            Path.Combine(_root.Path, "benchmarks", "artifacts", runId.ToString("N"), string.Concat(artifactId.ToString("N"), ".blob"));

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            _root.Dispose();
            _keyHolder.Dispose();
        }
    }
}
