namespace XE_Local_AI_Engine.Cli.Tests.Testing;

using System.Net;
using System.Text;

/// <summary>
///     Answers each request from a script and records what was sent. Hand-written because <c>SendAsync</c> is
///     protected, which a substitute cannot intercept.
/// </summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, HttpResponseMessage> _respond;

    public ScriptedHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public List<RecordedRequest> Requests { get; } = [];

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    public static string Tokens(string access, DateTimeOffset expires, string refresh) =>
        $$"""{"accessToken":"{{access}}","expiresAtUtc":"{{expires:O}}","refreshToken":"{{refresh}}","refreshTokenExpiresAtUtc":"{{expires.AddDays(7):O}}"}""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var recorded = new RecordedRequest
        {
            Method = request.Method.Method,
            Path = request.RequestUri!.PathAndQuery,
            Bearer = request.Headers.Authorization?.Parameter,
            RefreshHeader = request.Headers.TryGetValues("X-XE-Refresh-Token", out var values) ? values.Single() : null,
            HasCookie = request.Headers.Contains("Cookie"),
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)
        };
        Requests.Add(recorded);
        return _respond(recorded);
    }
}

internal sealed class RecordedRequest
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    public string? Bearer { get; init; }

    public string? RefreshHeader { get; init; }

    public bool HasCookie { get; init; }

    public string? Body { get; init; }

    public override string ToString() =>
        $"{Method} {Path}";
}
