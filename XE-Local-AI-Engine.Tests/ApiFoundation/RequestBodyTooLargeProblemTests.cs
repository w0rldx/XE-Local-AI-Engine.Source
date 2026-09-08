namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The endpoints' half of the one 413 answer. <c>RequestBodyTooLargeExceptionHandlerTests</c> pins the host's
///     half against the same writer; between them the two emitters are held to one status, one Content-Type and one
///     body shape, which is the contract <c>ProducesProblem(413)</c> declares on all six capped routes.
/// </summary>
public sealed class RequestBodyTooLargeProblemTests
{
    [Test]
    public async Task Result_WhenExecuted_WritesTheSameAnswerTheHostRefusalWrites()
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "trace-" + Guid.NewGuid().ToString("N"),
            Response =
            {
                Body = new MemoryStream()
            }
        };

        await RequestBodyTooLargeProblem.Result("The request body is larger than the 1 MB this node accepts for a graph.")
                                        .ExecuteAsync(context)
                                        .ConfigureAwait(false);

        AssertEx.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        AssertEx.Equal(RequestBodyTooLargeProblem.ContentType, context.Response.ContentType, "the endpoint path writes the header the host path writes.");

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body).ConfigureAwait(false);
        var body = document.RootElement;

        AssertEx.Equal(expected: 413, body.GetProperty("status").GetInt32());
        AssertEx.Equal("https://tools.ietf.org/html/rfc7231#section-6.5.11", body.GetProperty("type").GetString());
        AssertEx.Equal("Request body too large", body.GetProperty("title").GetString());
        AssertEx.NotEmpty(body.GetProperty("detail").GetString(), "the endpoint half names the cap it enforces.");
        AssertEx.True(body.TryGetProperty("traceId", out var traceId), "every problem body on this surface carries a traceId.");
        AssertEx.NotEmpty(traceId.GetString(), "an empty traceId correlates nothing.");
        AssertEx.False(body.TryGetProperty("errors", out _), "FastEndpoints' errors[] shape is the second shape for one status this replaced.");
    }
}
