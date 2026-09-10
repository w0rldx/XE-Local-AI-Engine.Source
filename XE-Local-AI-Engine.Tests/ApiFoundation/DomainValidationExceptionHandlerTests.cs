namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Models.NodeBinding;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Client.Services.Training.Export;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Client.Services.Workspace;
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

    /// <summary>
    ///     Every exception type promoted OUT of a per-endpoint <c>catch … AddError(exception.Message) +
    ///     Send.ErrorsAsync()</c> and INTO the switch. The route test above pins the body SHAPE once; this pins that
    ///     each promoted type actually reaches the switch and still answers 400 with its own message, which a shape
    ///     test over one route cannot show. Driven against the handler directly because these come from ten different
    ///     services, several of which need real infrastructure (Entra, Velopack, a GPU queue) to raise naturally.
    /// </summary>
    [Test]
    public async Task TryHandleAsync_ForEachPromotedValidationType_Writes400WithTheExceptionMessage()
    {
        Exception[] promoted =
        [
            new NodeBindingException("The node binding session expired."),
            new EntraConnectionNotConfiguredException("No Entra connection is configured."),
            new SkillImportException("The archive contains an entry outside the skill root."),
            new AppUpdateException("The update could not be applied."),
            new ExternalProviderValidationException("The connection id is not a valid slug."),
            new NodeChatInvalidBranchSelectionException(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            new EvaluationRejectedException("The training run held nothing back."),
            new TrainingExportRejectedException("The artifact has already been promoted."),
            new TrainingRunRejectedException("The base model licence was not confirmed."),
            new KnowledgeRepositoryImportRejectedException("The repository is past the configured file-count bound.")
        ];

        foreach (var exception in promoted)
        {
            var typeName = exception.GetType().Name;
            var context = new DefaultHttpContext
            {
                TraceIdentifier = "trace-" + Guid.NewGuid().ToString("N"),
                Request =
                {
                    Path = "/api/local/v1/promoted"
                },
                Response =
                {
                    Body = new MemoryStream()
                }
            };
            var handler = new DomainValidationExceptionHandler(NullLogger<DomainValidationExceptionHandler>.Instance);

            AssertEx.True(await handler.TryHandleAsync(context, exception, CancellationToken.None).ConfigureAwait(false),
                $"{typeName} must be answered by the global validation handler, not left to a per-endpoint catch.");
            AssertEx.Equal(expected: 400, context.Response.StatusCode, typeName);
            AssertEx.Contains(context.Response.ContentType, "problem+json", StringComparison.OrdinalIgnoreCase, typeName);

            context.Response.Body.Position = 0;
            using var document = await JsonDocument.ParseAsync(context.Response.Body).ConfigureAwait(false);
            var root = document.RootElement;
            var firstError = root.GetProperty("errors")[0];

            AssertEx.Equal(expected: 400, root.GetProperty("status").GetInt32(), typeName);
            AssertEx.Equal("/api/local/v1/promoted", root.GetProperty("instance").GetString(), typeName);
            AssertEx.Equal(context.TraceIdentifier, root.GetProperty("traceId").GetString(), typeName);
            AssertEx.Equal(expected: 1, root.GetProperty("errors").GetArrayLength(), typeName);
            // The field NAME the message hangs off, put through FastEndpoints' naming policy the way the writer does
            // — see FastEndpointsProblemBody. Config.Serializer is process-global, so over a bare context this lands
            // as the raw constant alone and camel-cased once any host in the process has run UseFastEndpoints; a
            // literal here would assert test order rather than the writer. The wire casing is pinned by the route
            // test above.
            AssertEx.Equal(FastEndpointsProblemBody.GeneralErrorsName, firstError.GetProperty("name").GetString(), typeName);
            AssertEx.Equal(exception.Message, firstError.GetProperty("reason").GetString(), typeName);
            AssertEx.Equal(exception.Message, root.GetProperty("detail").GetString(), typeName);
        }
    }

    /// <summary>
    ///     The two types the promotion deliberately LEFT at their endpoints, so a later "finish the job" pass cannot
    ///     quietly move them without this failing: <c>SelectedFolderValidationException</c> is the base of a 404 and a
    ///     409 type, and <c>DevelopmentWorkspaceSecurityException</c> is answered 409 by the patch/next-action
    ///     endpoints and 400 by the register/create ones. A global 400 would move real routes in both cases.
    /// </summary>
    [Test]
    public async Task TryHandleAsync_ForTheTypesWhoseStatusIsNotAlways400_DeclinesToHandleThem()
    {
        Exception[] excluded =
        [
            new SelectedFolderValidationException("The selected folder is not a Git repository root."),
            new DevelopmentWorkspaceSecurityException("The workspace path escapes the approved root.")
        ];

        foreach (var exception in excluded)
        {
            var context = new DefaultHttpContext
            {
                Response =
                {
                    Body = new MemoryStream()
                }
            };
            var handler = new DomainValidationExceptionHandler(NullLogger<DomainValidationExceptionHandler>.Instance);

            AssertEx.False(await handler.TryHandleAsync(context, exception, CancellationToken.None).ConfigureAwait(false),
                $"{exception.GetType().Name} answers more than one status across the endpoints that raise it, so the global 400 must not claim it.");
        }
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
