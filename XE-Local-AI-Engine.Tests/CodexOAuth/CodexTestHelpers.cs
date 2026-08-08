namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using System.Net;
using System.Text;
using System.Text.Json;

/// <summary>Shared builders for Codex OAuth tests: fake account JWTs and token-endpoint JSON.</summary>
internal static class CodexTestHelpers
{
    internal const string AccountId = "acct_test_123";

    /// <summary>
    ///     Builds an unsigned JWT whose payload carries the <c>chatgpt_account_id</c> claim under the
    ///     <c>https://api.openai.com/auth</c> namespace and an <c>exp</c>, matching what
    ///     <see cref="XE_Local_AI_Engine.Providers.CodexOAuth.Auth.CodexAuthService" /> decodes. Not a real signed
    ///     token — the service only base64url-decodes the payload, it does not verify the signature.
    /// </summary>
    internal static string BuildAccountJwt(string accountId = AccountId, DateTimeOffset? expiresUtc = null)
    {
        var exp = (expiresUtc ?? DateTimeOffset.UtcNow.AddHours(1)).ToUnixTimeSeconds();
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));
        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, string>
            {
                ["chatgpt_account_id"] = accountId
            },
            ["exp"] = exp
        });
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        return $"{header}.{payload}.signature-not-verified";
    }

    /// <summary>Builds a Codex token-endpoint success body (code exchange / refresh response).</summary>
    internal static string BuildTokenResponse(string accessToken, string refreshToken, int expiresInSeconds = 3600)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["refresh_token"] = refreshToken,
            ["expires_in"] = expiresInSeconds,
            ["token_type"] = "Bearer"
        });
    }

    internal static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace(oldChar: '+', newChar: '-').Replace(oldChar: '/', newChar: '_');
    }

    internal static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace(oldChar: '-', newChar: '+').Replace(oldChar: '_', newChar: '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded
        };
        return Convert.FromBase64String(padded);
    }
}

/// <summary>
///     A capturing <see cref="HttpMessageHandler" /> that records every request (method, URI, body) and returns a
///     queued canned response. Lets the auth-service tests drive code-exchange / refresh without real network I/O.
/// </summary>
internal sealed class CapturingHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

    public List<CapturedRequest> Requests { get; } = [];

    public void EnqueueJson(HttpStatusCode statusCode, string json)
    {
        _responders.Enqueue(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Requests.Add(new CapturedRequest(request.Method, request.RequestUri, body));

        var responder = _responders.Count > 0
            ? _responders.Dequeue()
            : _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        return responder(request);
    }
}

/// <summary>An immutable record of one captured outbound request.</summary>
internal sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string Body);
