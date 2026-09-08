namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the one refusal a REAL connection takes on a capped route. Kestrel enforces the body cap while it reads,
///     inside model binding, so the endpoint's own Content-Length exit never runs and its throw is what reaches the
///     pipeline. Unmapped, that throw was a 500 on every route that declares a 413.
/// </summary>
public sealed class RequestBodyTooLargeExceptionHandlerTests
{
    [Test]
    public async Task TryHandleAsync_WhenTheHostRefusedAnOversizedBody_WritesTheDeclared413ProblemBody()
    {
        var context = Context();
        var handler = new RequestBodyTooLargeExceptionHandler(NullLogger<RequestBodyTooLargeExceptionHandler>.Instance);

        // The exception Kestrel throws, synthesized: message and status are the two members it sets.
        var handled = await handler.TryHandleAsync(context,
                                       new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge),
                                       CancellationToken.None)
                                   .ConfigureAwait(false);

        AssertEx.True(handled);
        AssertEx.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        AssertEx.Contains(context.Response.ContentType, "application/problem+json", StringComparison.OrdinalIgnoreCase);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body).ConfigureAwait(false);
        AssertEx.Equal(expected: 413, document.RootElement.GetProperty("status").GetInt32());
        AssertEx.Equal("Request body too large", document.RootElement.GetProperty("title").GetString());

        // The same two literals the ENDPOINTS' own Content-Length exit answers with (see RequestBodyTooLargeAssert):
        // both halves build from RequestBodyTooLargeProblem, and pinning them on both sides is what keeps that true.
        AssertEx.Equal("https://tools.ietf.org/html/rfc7231#section-6.5.11", document.RootElement.GetProperty("type").GetString());
        AssertEx.NotEmpty(AssertEx.NotNull(document.RootElement.GetProperty("detail").GetString()));
        AssertEx.True(document.RootElement.TryGetProperty("traceId", out _), "every problem body on this surface carries a traceId.");
    }

    /// <summary>
    ///     The status is the discriminator rather than the type. The same exception carries the host's malformed-request
    ///     refusals, and answering 413 to a bad chunk or an over-long header would be a lie the client cannot act on.
    /// </summary>
    [Test]
    [Arguments(StatusCodes.Status400BadRequest, "a malformed request line")]
    [Arguments(StatusCodes.Status431RequestHeaderFieldsTooLarge, "headers over the host's own limit")]
    public async Task TryHandleAsync_ForAnyOtherBadHttpRequest_FallsThroughWithoutChangingTheResponse(int statusCode, string because)
    {
        var context = Context();
        var handler = new RequestBodyTooLargeExceptionHandler(NullLogger<RequestBodyTooLargeExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(context, new BadHttpRequestException("refused", statusCode), CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(handled, $"{because} is not an oversized body.");
        AssertEx.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        AssertEx.Null(context.Response.ContentType);
        AssertEx.Equal(expected: 0L, context.Response.Body.Length);
    }

    private static DefaultHttpContext Context() =>
        new()
        {
            TraceIdentifier = "trace-" + Guid.NewGuid().ToString("N"),
            Request =
            {
                Method = "POST",
                Path = "/api/local/v1/graph-workflows/definitions"
            },
            Response =
            {
                Body = new MemoryStream()
            }
        };
}
