namespace XE_Local_AI_Engine.Tests.Testing;

using System.Text.Json;
using XE_Local_AI_Engine.Client.ExceptionHandling;

/// <summary>
///     The ONE 413 body a capped route may answer with. Kestrel's mid-read refusal and the endpoint's own
///     Content-Length exit both DECLARE ASP.NET's <c>ProblemDetails</c> via <c>ProducesProblem(413)</c>.
/// </summary>
/// <remarks>
///     The endpoint half could send FastEndpoints' <c>errors[]</c> body, which the generated client cannot parse and a
///     status-only assertion cannot see, so these routes assert the shape. The Content-Type is pinned whole: both paths
///     run through <see cref="RequestBodyTooLargeProblem.WriteAsync" />, so any header difference means one stopped.
/// </remarks>
internal static class RequestBodyTooLargeAssert
{
    /// <param name="response">The refusal to inspect.</param>
    /// <param name="because">Names the route under test, so a failure says which of the capped routes drifted.</param>
    public static async Task DeclaredProblemShapeAsync(HttpResponseMessage response, string because)
    {
        ArgumentNullException.ThrowIfNull(response);

        AssertEx.Equal(RequestBodyTooLargeProblem.ContentType,
            response.Content.Headers.ContentType?.ToString(),
            $"{because} must answer the one Content-Type the shared writer sends, header string and all.");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;

        AssertEx.Equal(expected: 413, body.GetProperty("status").GetInt32(), because);
        AssertEx.Equal("https://tools.ietf.org/html/rfc7231#section-6.5.11", body.GetProperty("type").GetString(), because);
        AssertEx.Equal("Request body too large", body.GetProperty("title").GetString(), $"{because} must carry the same title the host's own refusal writes.");
        AssertEx.NotEmpty(body.GetProperty("detail").GetString(), $"{because} must name the cap it enforces.");
        AssertEx.True(body.TryGetProperty("traceId", out var traceId), $"{because}: every problem body on this surface carries a traceId.");
        AssertEx.NotEmpty(traceId.GetString(), $"{because}: an empty traceId correlates nothing.");
        AssertEx.False(body.TryGetProperty("errors", out _),
            $"{because} must not answer FastEndpoints' errors[] shape — that is the second shape for one status this replaced.");
    }
}
