namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the contract of <c>DomainValidationExceptionHandler</c>: the 400 it writes for a single-message domain
///     validation exception must be byte-compatible with the endpoint-local <c>AddError(message) +
///     Send.ErrorsAsync()</c> pair it replaced, because the SPA and the generated client already read that shape.
///     Both bodies are produced by the SAME route — POST model-fit/recommendations/refresh — so <c>instance</c> is
///     directly comparable: an unsupported <c>useCase</c> still takes the endpoint's own AddError path (FastEndpoints
///     writes the body), while an unknown scheduled-job id makes the trigger throw ScheduledJobValidationException,
///     which now reaches the global handler. Only the message and the per-request traceId may differ.
/// </summary>
public sealed class DomainValidationExceptionHandlerTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    private const string RefreshRoute = "/api/local/v1/model-fit/recommendations/refresh";

    [Test]
    public async Task GlobalHandlerBody_IsByteCompatibleWithEndpointLocalErrorsAsyncBody()
    {
        // FastEndpoints' own body: the endpoint rejects the use case itself with AddError + Send.ErrorsAsync.
        var local = await PostAsync(new
        {
            scheduledJobId = Guid.NewGuid(),
            useCase = "definitely-not-a-supported-use-case"
        }).ConfigureAwait(false);

        // The global handler's body: a random job id resolves to no definition, so the trigger throws
        // ScheduledJobValidationException out of HandleAsync (the endpoint no longer catches it).
        var global = await PostAsync(new
        {
            scheduledJobId = Guid.NewGuid()
        }).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, local.StatusCode);
        AssertEx.Equal(HttpStatusCode.BadRequest, global.StatusCode);
        AssertEx.Equal(local.ContentType, global.ContentType);
        AssertEx.Contains(local.ContentType, "problem+json", StringComparison.OrdinalIgnoreCase);

        // Field-by-field, so a regression names the field that drifted rather than dumping two json blobs.
        AssertEx.Equal(local.Status, global.Status);
        AssertEx.Equal(local.Title, global.Title);
        AssertEx.Equal(local.Type, global.Type);
        AssertEx.Equal(local.Instance, global.Instance);
        AssertEx.Equal(RefreshRoute, global.Instance);
        AssertEx.Equal(local.ErrorName, global.ErrorName);
        AssertEx.Equal("generalErrors", global.ErrorName);
        AssertEx.Equal(local.ErrorCount, global.ErrorCount);
        AssertEx.Equal(expected: 1, global.ErrorCount);

        // FE 8.2's default DetailTransformer copies the single error's reason into detail — the handler must too.
        AssertEx.Equal(local.Detail, local.ErrorReason);
        AssertEx.Equal(global.Detail, global.ErrorReason);
        AssertEx.NotEmpty(global.ErrorReason);
        AssertEx.NotEmpty(global.TraceId);
        AssertEx.NotEmpty(local.TraceId);

        // Whole-payload equality once the two values that are allowed to differ (the message and the per-request
        // trace id) are masked: property set, property order and formatting must all match, not just the values.
        AssertEx.Equal(Canonicalize(local), Canonicalize(global));
    }

    private static string Canonicalize(ProblemBody body)
    {
        return body.Json
                   .Replace(body.ErrorReason, "<message>", StringComparison.Ordinal)
                   .Replace(body.TraceId, "<traceId>", StringComparison.Ordinal);
    }

    private async Task<ProblemBody> PostAsync(object payload)
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, RefreshRoute)
        {
            Content = JsonContent.Create(payload)
        };
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var errors = root.GetProperty("errors");
        var firstError = errors[0];

        return new ProblemBody
        {
            StatusCode = response.StatusCode,
            ContentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty,
            Json = json,
            Status = root.GetProperty("status").GetInt32(),
            Title = root.GetProperty("title").GetString() ?? string.Empty,
            Type = root.GetProperty("type").GetString() ?? string.Empty,
            Instance = root.GetProperty("instance").GetString() ?? string.Empty,
            TraceId = root.GetProperty("traceId").GetString() ?? string.Empty,
            Detail = root.GetProperty("detail").GetString() ?? string.Empty,
            ErrorCount = errors.GetArrayLength(),
            ErrorName = firstError.GetProperty("name").GetString() ?? string.Empty,
            ErrorReason = firstError.GetProperty("reason").GetString() ?? string.Empty
        };
    }

    private sealed class ProblemBody
    {
        public required HttpStatusCode StatusCode { get; init; }

        public required string ContentType { get; init; }

        public required string Json { get; init; }

        public required int Status { get; init; }

        public required string Title { get; init; }

        public required string Type { get; init; }

        public required string Instance { get; init; }

        public required string TraceId { get; init; }

        public required string Detail { get; init; }

        public required int ErrorCount { get; init; }

        public required string ErrorName { get; init; }

        public required string ErrorReason { get; init; }
    }
}
