namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins what the Development area got when its exception mapping moved out of every endpoint and into the global
///     chain: a missing entity is still the surface's bodyless 404, and the two Development conflicts still write the
///     exact <c>AddError(message) + Send.ErrorsAsync(statusCode: 409)</c> body their endpoints used to write by hand.
/// </summary>
public sealed class DevelopmentExceptionHandlerTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    /// <summary>
    ///     End to end over a real route: the store raises <see cref="DevelopmentNotFoundException" /> for an id that
    ///     does not exist, no endpoint catches it any more, and the answer is still an empty-bodied 404.
    /// </summary>
    [Test]
    public async Task GetProject_ForAnIdThatDoesNotExist_StillAnswersABodylessNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/local/v1/development/projects/{Guid.NewGuid()}");
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEx.Equal(string.Empty, body, "the 404 the Development endpoints used to send carried no body, and the global handler must not add one.");
    }

    /// <summary>
    ///     The not-found handler answers the TYPED family only. A bare <see cref="KeyNotFoundException" /> — a
    ///     dictionary miss anywhere else in the pipeline — must still reach the 500 that says something is broken,
    ///     which is the whole reason the store stopped throwing the CLR type.
    /// </summary>
    [Test]
    public async Task NotFoundHandler_ForABareKeyNotFoundException_DeclinesToHandleIt()
    {
        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream()
            }
        };
        var handler = new DevelopmentNotFoundExceptionHandler();

        AssertEx.False(await handler.TryHandleAsync(context, new KeyNotFoundException("an unrelated dictionary miss"), CancellationToken.None).ConfigureAwait(false));
    }

    [Test]
    public async Task NotFoundHandler_ForTheTypedFamily_Writes404WithNoBody()
    {
        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream()
            }
        };
        var handler = new DevelopmentNotFoundExceptionHandler();

        AssertEx.True(await handler.TryHandleAsync(context, new DevelopmentNotFoundException("Development project 'x' was not found."), CancellationToken.None)
                                   .ConfigureAwait(false));
        AssertEx.Equal(expected: 404, context.Response.StatusCode);
        AssertEx.Equal(expected: 0L, context.Response.Body.Length);
    }

    /// <summary>
    ///     The two conflict types whose endpoints all answered 409 identically. The body asserted here is the one
    ///     <c>Send.ErrorsAsync(statusCode: 409)</c> produced: the message on the general-errors field, mirrored into
    ///     <c>detail</c>, at status 409 — a relocation of the mapping, not a new envelope.
    /// </summary>
    [Test]
    [Arguments("invalid transition")]
    [Arguments("concurrency")]
    public async Task ConflictHandler_ForTheDevelopmentConflictFamily_WritesTheSame409BodyTheEndpointsWrote(string kind)
    {
        Exception exception = kind == "invalid transition"
            ? new DevelopmentInvalidTransitionException("The task is Applied, so there is no next action to start.")
            : new DevelopmentConcurrencyException("The Development project version is stale (expected 3, current 4).");

        var context = new DefaultHttpContext
        {
            TraceIdentifier = "trace-" + Guid.NewGuid().ToString("N"),
            Request =
            {
                Path = "/api/local/v1/development/projects/p/tasks/t/next-action"
            },
            Response =
            {
                Body = new MemoryStream()
            }
        };
        var handler = new DevelopmentConflictExceptionHandler(NullLogger<DevelopmentConflictExceptionHandler>.Instance);

        AssertEx.True(await handler.TryHandleAsync(context, exception, CancellationToken.None).ConfigureAwait(false));
        AssertEx.Equal(expected: 409, context.Response.StatusCode);
        AssertEx.Contains(context.Response.ContentType, "problem+json", StringComparison.OrdinalIgnoreCase);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body).ConfigureAwait(false);
        var root = document.RootElement;

        // The status-derived pair, pinned so "the body did not change" is checkable rather than asserted. Note the
        // base: FastEndpoints derives www.rfc-editor.org/rfc/, while ConflictExceptionHandler hand-writes
        // tools.ietf.org/html/ for this same 409. Same RFC and section, two different URI bases on one API — a real
        // inconsistency, and out of scope to fix here because either side is a wire change.
        AssertEx.Equal("https://www.rfc-editor.org/rfc/rfc7231#section-6.5.8", root.GetProperty("type").GetString());
        AssertEx.Equal("Conflict", root.GetProperty("title").GetString());
        AssertEx.Equal(expected: 409, root.GetProperty("status").GetInt32());
        AssertEx.Equal("/api/local/v1/development/projects/p/tasks/t/next-action", root.GetProperty("instance").GetString());
        AssertEx.Equal(context.TraceIdentifier, root.GetProperty("traceId").GetString());
        AssertEx.Equal(exception.Message, root.GetProperty("detail").GetString());
        AssertEx.Equal(expected: 1, root.GetProperty("errors").GetArrayLength());
        AssertEx.Equal(exception.Message, root.GetProperty("errors")[0].GetProperty("reason").GetString());

        // The field name goes through FastEndpoints' naming policy the way the writer applies it — see
        // FastEndpointsProblemBody. Config.Serializer is process-global, so over a bare context this is the raw
        // constant alone and camel-cased once any host in the process has run UseFastEndpoints; either literal would
        // assert test order rather than the writer.
        AssertEx.Equal(FastEndpointsProblemBody.GeneralErrorsName, root.GetProperty("errors")[0].GetProperty("name").GetString());

        // It must NOT have become the ConflictProblemDetails envelope: that body carries a conflictType discriminator
        // and puts "Conflict" in title, which is a wire change the SPA and the generated client have not been told about.
        AssertEx.False(root.TryGetProperty("conflictType", out _),
            "the Development 409 keeps the FastEndpoints problem body it always had; folding it into the conflict envelope is a separate, OpenAPI-visible change.");
    }

    /// <summary>
    ///     <c>DevelopmentWorkspaceSecurityException</c> is answered 409 by the patch and next-action endpoints and 400
    ///     by the register/create/reconnect ones, so neither global handler may claim it — and its derived
    ///     <c>DevelopmentRepositoryStateConflictException</c> would be swallowed by the reconnect endpoint's own
    ///     base-type catch before a handler ever saw it.
    /// </summary>
    [Test]
    public async Task ConflictHandler_ForTheWorkspaceSecurityFamily_DeclinesToHandleIt()
    {
        Exception[] excluded =
        [
            new DevelopmentWorkspaceSecurityException("The workspace path escapes the approved root."),
            new DevelopmentRepositoryStateConflictException("The bound repository identity no longer matches.")
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
            var handler = new DevelopmentConflictExceptionHandler(NullLogger<DevelopmentConflictExceptionHandler>.Instance);

            AssertEx.False(await handler.TryHandleAsync(context, exception, CancellationToken.None).ConfigureAwait(false),
                $"{exception.GetType().Name} does not have one status across the endpoints that raise it.");
        }
    }
}
