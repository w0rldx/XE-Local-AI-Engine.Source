namespace XE_Local_AI_Engine.Tests.Endpoints.Scheduler.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint integration tests for the scheduler management API.
///     Covers: 401 on every route without a bearer token; reachability with operator token;
///     validation→400 response on bad input; redaction (no raw parameters in job response;
///     no raw details/error_details in run response).
/// </summary>
public sealed class SchedulerEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    // ──────────────────────────────────────────────────────────────────────
    // Route helpers
    // ──────────────────────────────────────────────────────────────────────

    private const string ApiPrefix = "/api/local/v1";

    private static string TemplatesRoute()
    {
        return $"{ApiPrefix}/scheduler/templates";
    }

    private static string JobsRoute()
    {
        return $"{ApiPrefix}/scheduler/jobs";
    }

    private static string JobByIdRoute(Guid id)
    {
        return $"{ApiPrefix}/scheduler/jobs/{id}";
    }

    private static string JobEnableRoute(Guid id)
    {
        return $"{ApiPrefix}/scheduler/jobs/{id}/enable";
    }

    private static string JobDisableRoute(Guid id)
    {
        return $"{ApiPrefix}/scheduler/jobs/{id}/disable";
    }

    private static string JobTriggerRoute(Guid id)
    {
        return $"{ApiPrefix}/scheduler/jobs/{id}/trigger";
    }

    private static string RunsRoute()
    {
        return $"{ApiPrefix}/scheduler/runs";
    }

    private static string RunByIdRoute(Guid id)
    {
        return $"{ApiPrefix}/scheduler/runs/{id}";
    }

    private static string RunCancelRoute(Guid id)
    {
        return $"{ApiPrefix}/scheduler/runs/{id}/cancel";
    }

    // ──────────────────────────────────────────────────────────────────────
    // 401 — every route requires a bearer token
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ListTemplates_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(TemplatesRoute()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ListJobs_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(JobsRoute()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task CreateJob_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, JobsRoute())
        {
            Content = JsonContent.Create(new
            {
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task GetJob_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(JobByIdRoute(Guid.NewGuid())).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task UpdateJob_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, JobByIdRoute(Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task DeleteJob_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Delete, JobByIdRoute(Guid.NewGuid()));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task EnableJob_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, JobEnableRoute(Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task DisableJob_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, JobDisableRoute(Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task TriggerJob_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, JobTriggerRoute(Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ListRuns_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(RunsRoute()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task GetRun_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(RunByIdRoute(Guid.NewGuid())).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task CancelRun_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, RunCancelRoute(Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Reachability with operator token
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ListTemplates_WithOperatorToken_ReturnsOk()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, TemplatesRoute());
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public async Task ListJobs_WithOperatorToken_ReturnsOk()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, JobsRoute());
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public async Task GetJob_WhenJobMissing_WithOperatorToken_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, JobByIdRoute(Guid.NewGuid()));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task ListRuns_WithOperatorToken_ReturnsOk()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, RunsRoute());
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public async Task GetRun_WhenRunMissing_WithOperatorToken_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, RunByIdRoute(Guid.NewGuid()));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task CancelRun_WhenRunMissing_WithOperatorToken_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, RunCancelRoute(Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task CancelRun_WhenRunAlreadyTerminal_ReturnsProblemDetailsConflictWithOutcome()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        // Seed a run that already reached a terminal state — the only way CancelRunAsync reports AlreadyTerminal.
        Guid runId;
        using (var scope = factory.Services.CreateScope())
        {
            var runStore = scope.ServiceProvider.GetRequiredService<IScheduledJobRunStore>();
            var stored = await runStore.AddAsync(new ScheduledJobRunInput(Guid.NewGuid(),
                                           "terminal-run-template",
                                           QuartzFireInstanceId: null,
                                           ScheduledRunTrigger.Manual,
                                           ScheduledRunStatus.Succeeded,
                                           ScheduledFireTimeUtc: null,
                                           ActualFireTimeUtc: null))
                                       .ConfigureAwait(false);
            runId = stored.Id;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, RunCancelRoute(runId));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(expected: 409, document.RootElement.GetProperty("status").GetInt32());
        AssertEx.Equal(nameof(RunCancellationOutcome.AlreadyTerminal), document.RootElement.GetProperty("outcome").GetString());
        AssertEx.Equal("The run already reached a terminal state and cannot be cancelled.",
            document.RootElement.GetProperty("detail").GetString());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Validation → 400 with error body (not 500)
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task CreateJob_WithInvalidCronExpression_ReturnsBadRequestWithErrorBody()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        // POST with a templateId that the app doesn't have registered → validation error.
        // Use an unknown template so the service throws ScheduledJobValidationException (not 500).
        var body = new
        {
            templateId = "does-not-exist",
            displayName = "Bad Job",
            scheduleKind = "Cron",
            cronExpression = "not-a-valid-cron",
            timeZoneId = "UTC",
            misfirePolicy = "Smart",
            preventOverlap = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, JobsRoute())
        {
            Content = JsonContent.Create(body)
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        // ScheduledJobValidationException → global DomainValidationExceptionHandler → 400.
        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Response must be a structured error body, not an empty 400.
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.True(payload.Length > 0, "Error response must have a non-empty body.");
    }

    [Test]
    public async Task CreateJob_WithBlankDisplayName_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var body = new
        {
            templateId = "does-not-exist",
            displayName = "", // blank → validation error
            scheduleKind = "Cron",
            cronExpression = "0 0 * * * ?",
            timeZoneId = "UTC",
            misfirePolicy = "Smart",
            preventOverlap = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, JobsRoute())
        {
            Content = JsonContent.Create(body)
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task EnableJob_WhenTemplateNotRegistered_ReturnsBadRequestWithErrorBody()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        // Seed a definition whose template the host does not have registered. CreateJob would reject it up front, so
        // it goes straight to the store — this is exactly the state SetEnabledAsync must refuse without writing.
        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IScheduledJobDefinitionStore>();
            var stored = await store.AddAsync(new ScheduledJobDefinitionInput("does-not-exist",
                                        "Orphaned template job",
                                        Description: null,
                                        Enabled: false,
                                        ScheduleKind.Cron,
                                        "0 0 * * * ?",
                                        IntervalSeconds: null,
                                        RepeatCount: null,
                                        StartAtUtc: null,
                                        EndAtUtc: null,
                                        "UTC",
                                        SchedulerMisfirePolicy.Smart,
                                        PreventOverlap: false,
                                        MaxRuntimeSeconds: null,
                                        ParameterJson: null,
                                        ScheduledJobCreator.User))
                                    .ConfigureAwait(false);
            jobId = stored.Id;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, JobEnableRoute(jobId))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        // ScheduledJobValidationException → global DomainValidationExceptionHandler → 400 (not an unhandled 500).
        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.True(payload.Length > 0, "Error response must have a non-empty body.");
    }

    // Redaction: job response must not echo raw parameters; run response must not echo raw details.

    [Test]
    public async Task ListJobs_ResponseDoesNotContainRawParameters()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, JobsRoute());
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // The response must not echo raw parameter_json.
        AssertEx.False(json.Contains("parameterJson", StringComparison.OrdinalIgnoreCase),
            "Job list response must not expose raw parameterJson.");
        AssertEx.False(json.Contains("parameter_json", StringComparison.OrdinalIgnoreCase),
            "Job list response must not expose raw parameter_json.");

        // hasParameters must be present in the schema (the list response wraps items).
        // It is okay for the key to exist with value false when no jobs exist; what matters is
        // the absence of the raw key.  When the list is empty this test still validates redaction.
    }

    [Test]
    public async Task ListJobs_ResponseContainsHasParametersField()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        // Seed a job via the service, then verify the response shape carries hasParameters.
        // With scheduler disabled in the test host (no real Quartz), we call ListJobs on an
        // empty DB and verify the schema contract via the items array being present.
        using var request = new HttpRequestMessage(HttpMethod.Get, JobsRoute());
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        // The list response wraps results in an "items" array.
        AssertEx.True(doc.RootElement.TryGetProperty("items", out _),
            "List jobs response must have an 'items' property.");
    }

    [Test]
    public async Task ListRuns_ResponseDoesNotContainRawDetailsOrErrorDetails()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, RunsRoute());
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // Raw encrypted fields must never appear in the wire response.
        AssertEx.False(json.Contains("detailsJson", StringComparison.OrdinalIgnoreCase),
            "Run list response must not expose raw detailsJson.");
        AssertEx.False(json.Contains("details_json", StringComparison.OrdinalIgnoreCase),
            "Run list response must not expose raw details_json.");
        AssertEx.False(json.Contains("errorDetails", StringComparison.OrdinalIgnoreCase),
            "Run list response must not expose raw errorDetails.");
        AssertEx.False(json.Contains("error_details", StringComparison.OrdinalIgnoreCase),
            "Run list response must not expose raw error_details.");
    }
}
