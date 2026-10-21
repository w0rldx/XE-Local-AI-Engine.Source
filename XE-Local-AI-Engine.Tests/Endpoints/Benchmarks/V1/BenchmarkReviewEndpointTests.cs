namespace XE_Local_AI_Engine.Tests.Endpoints.Benchmarks.V1;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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
using XE_Local_AI_Engine.Tests.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The review routes over the real review service and a substituted store: what the host owns is the route, the policy, the body mapping and the
///     error mapping, and the service in between is the one the node runs.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class BenchmarkReviewEndpointTests
{
    private const string Api = "/api/local/v1/benchmarks";
    private static readonly Guid ProjectId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid RunId = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid RevisionId = new("00000000-0000-0000-0000-000000000003");

    [Test]
    public async Task PutReview_WhenNotOperator_Returns403()
    {
        await using var context = new Context();
        using var client = context.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, Api + $"/runs/{RunId}/review")
        {
            Content = JsonContent.Create(new
            {
                state = "approved",
                score = 70,
                expectedVersion = 3
            })
        };
        context.Factory.AddNonOperatorBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _ = context.Store.DidNotReceive().SubmitReviewAsync(Arg.Any<BenchmarkReviewCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PutReview_WhenValid_ReturnsDetailWithReview()
    {
        // The positive control for the 403 above. No judge policy, so the reviewer gives the overall alone and the revision stamp is null.
        await using var context = new Context();
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Project());
        context.Store.GetRunAsync(RunId, Arg.Any<CancellationToken>()).Returns(Run());
        BenchmarkReviewCommand? submitted = null;
        context.Store.SubmitReviewAsync(Arg.Do<BenchmarkReviewCommand>(command => submitted = command), Arg.Any<CancellationToken>())
               .Returns(Run() with
               {
                   Version = 4,
                   UserScore = 70,
                   ToolCallCount = 1,
                   ToolTimeMs = 15,
                   Review = new BenchmarkRunReviewRecord
                   {
                       Id = Guid.NewGuid(),
                       RunId = RunId,
                       State = BenchmarkReviewStates.Approved,
                       Score = 70,
                       Criteria = [],
                       PolicyRevisionId = null,
                       Notes = "reads well",
                       Version = 1,
                       CreatedAtUtc = 50,
                       UpdatedAtUtc = 60
                   }
               });
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Put, Api + $"/runs/{RunId}/review", new
        {
            state = "approved",
            score = 70,
            notes = "reads well",
            expectedVersion = 3
        });

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, body);
        var command = AssertEx.NotNull(submitted);
        AssertEx.Equal(RunId, command.RunId);
        AssertEx.Equal(expected: 3, command.ExpectedRunVersion);
        AssertEx.Equal(BenchmarkReviewStates.Approved, command.State);
        AssertEx.Equal(expected: 70, command.Score);
        AssertEx.Equal("reads well", command.Notes);
        AssertEx.Null(command.PolicyRevisionId);
        using var document = JsonDocument.Parse(body);
        var review = document.RootElement.GetProperty("review");
        AssertEx.Equal("approved", review.GetProperty("state").GetString());
        AssertEx.Equal(expected: 70, review.GetProperty("score").GetInt32());
        AssertEx.Equal("reads well", review.GetProperty("notes").GetString());
        AssertEx.True(review.GetProperty("policyCurrent").GetBoolean(), "Reviewed without a policy, and the project still has none.");
        AssertEx.Equal(expected: 60, review.GetProperty("updatedAtUtc").GetInt64());
        AssertEx.Equal(expected: 70, document.RootElement.GetProperty("userScore").GetInt32());
        AssertEx.Equal(expected: 1, document.RootElement.GetProperty("toolCallCount").GetInt32());
        AssertEx.Equal(expected: 15, document.RootElement.GetProperty("toolTimeMs").GetInt64());
        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("artifacts").GetArrayLength());
    }

    [Test]
    public async Task GetRun_WhenTheProjectsPolicyMovedOn_ReportsTheReviewAsNotCurrent()
    {
        await using var context = new Context();
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Project() with
        {
            CurrentJudgePolicyRevisionId = Guid.NewGuid()
        });
        context.Store.GetRunAsync(RunId, Arg.Any<CancellationToken>()).Returns(Run() with
        {
            Review = Review(RevisionId)
        });
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/runs/{RunId}");

        using var response = await client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var review = document.RootElement.GetProperty("review");
        AssertEx.Equal(RevisionId.ToString("D"), review.GetProperty("policyRevisionId").GetString());
        AssertEx.False(review.GetProperty("policyCurrent").GetBoolean(), "A decision under a superseded revision is stale.");
    }

    [Test]
    public async Task PutScore_WhenProjectRequiresReview_Returns409()
    {
        await using var context = new Context();
        context.Store.SetUserScoreAsync(RunId, 55, 3, Arg.Any<CancellationToken>())
               .Returns<Task<BenchmarkRunRecord>>(_ => throw new BenchmarkConflictException("ReviewRequired"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Put, Api + $"/runs/{RunId}/score", new
        {
            score = 55,
            expectedVersion = 3
        });

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, BenchmarkErrorCode.ReviewRequired);
    }

    [Test]
    public async Task DeleteScore_WhenProjectRequiresReview_Returns409()
    {
        // The clear goes through the same SetUserScoreAsync guard, so a review project's approved score cannot be cleared around the review either.
        await using var context = new Context();
        context.Store.SetUserScoreAsync(RunId, null, 3, Arg.Any<CancellationToken>())
               .Returns<Task<BenchmarkRunRecord>>(_ => throw new BenchmarkConflictException("ReviewRequired"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Delete, Api + $"/runs/{RunId}/score", new
        {
            expectedVersion = 3
        });

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, BenchmarkErrorCode.ReviewRequired);
    }

    [Test]
    public async Task PutReview_WhenJudgingFailed_Returns409NotReviewable()
    {
        // Under a judge policy a run is reviewable only once its current judging succeeded: the reviewer re-judges first, never overrules a failure.
        await using var context = new Context();
        context.Store.GetRunAsync(RunId, Arg.Any<CancellationToken>()).Returns(Run(judgeState: BenchmarkRunJudgeStates.Failed));
        context.Store.GetCurrentJudgePolicyRevisionAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns(new BenchmarkJudgePolicyRevisionRecord
               {
                   Id = RevisionId,
                   ProjectId = ProjectId,
                   Revision = 1,
                   PolicyJson = BenchmarkJudgeSerialization.SerializePolicy(BenchmarkJudgeExecutorTests.Policy()),
                   PolicyHash = new string('a', count: 64),
                   ReferenceExecutionKey = null,
                   CohortGeneration = 1,
                   CreatedAtUtc = 1
               });
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Put, Api + $"/runs/{RunId}/review", new
        {
            state = "approved",
            criteria = new[]
            {
                new
                {
                    criterionId = "correctness",
                    score = 7
                }
            },
            expectedVersion = 3
        });

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, BenchmarkErrorCode.NotReviewable);
        _ = context.Store.DidNotReceive().SubmitReviewAsync(Arg.Any<BenchmarkReviewCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PutReview_WithACriterionMissingItsScore_Returns400()
    {
        await using var context = new Context();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Put, Api + $"/runs/{RunId}/review", new
        {
            state = "approved",
            criteria = new[]
            {
                new
                {
                    criterionId = "correctness"
                }
            },
            expectedVersion = 3
        });

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _ = context.Store.DidNotReceive().GetRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PutReview_WhenNotesTooLong_Returns400()
    {
        await using var context = new Context();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Put, Api + $"/runs/{RunId}/review", new
        {
            state = "rejected",
            notes = new string('n', BenchmarkReviewLimits.MaxNotesCharacters + 1),
            expectedVersion = 3
        });

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, body);
        AssertEx.Contains(body, BenchmarkReviewLimits.MaxNotesCharacters.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        _ = context.Store.DidNotReceive().GetRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetReviews_Pending_ListsOnlyReviewableRuns()
    {
        // Which runs are reviewable is the store's predicate; the route's job is to ask for exactly the requested state, project and page.
        await using var context = new Context();
        BenchmarkReviewQuery? asked = null;
        context.Store.ListReviewQueueAsync(Arg.Do<BenchmarkReviewQuery>(query => asked = query), Arg.Any<CancellationToken>())
               .Returns(new BenchmarkReviewQueuePage
               {
                   Items =
                   [
                       new BenchmarkReviewQueueEntry
                       {
                           RunId = RunId,
                           ProjectId = ProjectId,
                           RunVersion = 3,
                           PrimaryModelName = "model",
                           TaskItemId = null,
                           TaskItemIndex = null,
                           PrimaryCompletedAtUtc = 40,
                           State = BenchmarkReviewStates.Pending,
                           ReviewScore = null,
                           ReviewedAtUtc = null
                       }
                   ],
                   TotalCount = 21
               });
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/reviews?state=pending&projectId={ProjectId}&page=3&take=10");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, body);
        var query = AssertEx.NotNull(asked);
        AssertEx.Equal(BenchmarkReviewStates.Pending, query.State);
        AssertEx.Equal(ProjectId, query.ProjectId);
        AssertEx.Equal(expected: 20, query.Skip);
        AssertEx.Equal(expected: 10, query.Take);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(expected: 21, document.RootElement.GetProperty("totalCount").GetInt32());
        var item = document.RootElement.GetProperty("items")[0];
        AssertEx.Equal(RunId.ToString("D"), item.GetProperty("runId").GetString());
        AssertEx.Equal(expected: 3, item.GetProperty("runVersion").GetInt64());
        AssertEx.Equal("pending", item.GetProperty("state").GetString());
    }

    [Test]
    public async Task GetReviews_WithAnUnknownState_Returns400()
    {
        // The real store refuses the state; the substitute stands in for that refusal.
        await using var context = new Context();
        context.Store.ListReviewQueueAsync(Arg.Any<BenchmarkReviewQuery>(), Arg.Any<CancellationToken>())
               .Returns<Task<BenchmarkReviewQueuePage>>(_ => throw new BenchmarkValidationException("Review state must be pending, approved, rejected or stale."));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + "/reviews?state=maybe");

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, BenchmarkErrorCode.InvalidRequest);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, BenchmarkErrorCode code)
    {
        var body = await response.Content.ReadAsStringAsync();
        AssertEx.Equal(status, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(code.ToString(), document.RootElement.GetProperty("code").GetString());
    }

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

    private static BenchmarkRunReviewRecord Review(Guid? policyRevisionId) =>
        new()
        {
            Id = Guid.NewGuid(),
            RunId = RunId,
            State = BenchmarkReviewStates.Approved,
            Score = 70,
            Criteria = [],
            PolicyRevisionId = policyRevisionId,
            Notes = null,
            Version = 1,
            CreatedAtUtc = 50,
            UpdatedAtUtc = 60
        };

    private static BenchmarkProjectRecord Project() =>
        new()
        {
            Id = ProjectId,
            Name = "Project",
            CoreTaskJson = Encoding.UTF8.GetBytes("\"Answer exactly.\""),
            ContextTokens = 4096,
            AgentDefinitionId = Guid.NewGuid(),
            JudgeEnabled = false,
            CurrentJudgePolicyRevisionId = null,
            IsFrozen = true,
            Version = 4,
            CreatedAtUtc = 10,
            UpdatedAtUtc = 20,
            MaxOutputTokens = null,
            InvocationTimeoutSeconds = null,
            ReasoningBudgetTokens = null,
            FidelityEnabled = false,
            FidelityKldEnabled = false,
            FidelityChunks = null,
            FidelityKldBaseModelName = null,
            FidelityKldBaseFingerprint = null,
            RequiresReview = true
        };

    private static BenchmarkRunRecord Run(string judgeState = BenchmarkRunJudgeStates.None) =>
        new()
        {
            Id = RunId,
            ProjectId = ProjectId,
            RuntimeSnapshotJson = Encoding.UTF8.GetBytes("secret-runtime"),
            PrimaryModelName = "model",
            PrimaryModelOrigin = LocalModelOrigin.Imported,
            ModelContentFingerprint = "v1:aggregate",
            AgentName = "Agent",
            AgentDefinitionId = Guid.Empty,
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
            UpdatedAtUtc = 20,
            Judge = new BenchmarkRunJudgeView
            {
                State = judgeState,
                AttemptId = judgeState == BenchmarkRunJudgeStates.None ? null : Guid.NewGuid(),
                Score = null,
                PolicyRevision = null,
                PolicyRevisionId = judgeState == BenchmarkRunJudgeStates.None ? null : RevisionId,
                AttemptSequence = null,
                CohortGeneration = null,
                ExecutionKey = null,
                ErrorMessage = null,
                PolicyCurrent = judgeState != BenchmarkRunJudgeStates.None,
                ExecutionCurrent = false,
                RankExclusionReason = BenchmarkRunJudgeStates.ReasonReviewPending
            }
        };

    private sealed class Context : IAsyncDisposable
    {
        public Context() =>
            Factory = new TestServerWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<IBenchmarkStore>();
                    services.AddSingleton(Store);
                }
            };

        public IBenchmarkStore Store { get; } = Substitute.For<IBenchmarkStore>();

        public TestServerWebAppFactory Factory { get; }

        public ValueTask DisposeAsync() =>
            Factory.DisposeAsync();
    }
}
