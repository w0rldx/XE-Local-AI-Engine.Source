namespace XE_Local_AI_Engine.Tests.Invocation;

using System.ClientModel;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins how <see cref="InvocationFailureClassifier.MapFailure" /> names an OpenAI-compatible provider or gateway
///     rejection (<see cref="ClientResultException" /> by status) and an Azure auth failure, quoting only the body's sanitized
///     code and message.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class InvocationFailureClassifierTests
{
    private const string AuthFailedMessage = "The provider rejected the credentials. Replace the API key, bearer token or secret headers in the connection settings.";

    private const string ServerErrorMessage = "The provider returned a server error (HTTP 500 Internal Server Error). Retry, or check the connection's gateway.";

    private const string TokenRejectedMessage = "The provider rejected the token; it may have expired. Get a new token and replace it in the connection settings.";

    private const string NoRouteMessage = "The gateway has no route for this request. Check the connection's base URL; it usually ends in /openai/v1.";

    private const string BackendNotPermittedMessage = "The gateway's backend is not permitted to use this model or operation. Contact the gateway admin.";

    private const string ModelNotOfferedMessage = "The provider does not offer the selected model. Check the model id on the connection.";

    private const string UnsupportedOperationBody = """{"error":{"message":"The requested operation is unsupported."}}""";

    private const string TenantId = "tenant-0f3c9a";
    private const string UserId = "user-7b21de";
    private const string ProjectId = "project-44aa10";

    // A policy gateway's 403 echoes the caller's ids beside the reason; only the reason may reach the operator.
    private const string PolicyBodyTemplate =
        $$"""{"allowed":false,"reason":"{0}","tenantId":"{{TenantId}}","userId":"{{UserId}}","projectId":"{{ProjectId}}","modelId":"DeepSeek-V4-Flash-2","foundryInstanceId":"","tokensPerMinute":0,"projectEndDateUtc":null}""";

    [Test]
    [Arguments(401, null, FailureCategory.ProviderAuthFailed, AuthFailedMessage)]
    [Arguments(403, null, FailureCategory.ProviderAuthFailed, AuthFailedMessage)]
    [Arguments(404, null, FailureCategory.ModelUnavailable, "The provider does not offer the selected model. Check the model id on the connection.")]
    [Arguments(429, null, FailureCategory.ProviderRateLimited, "The provider rate-limited this request.")]
    [Arguments(429, "30", FailureCategory.ProviderRateLimited, "The provider rate-limited this request, retry after 30.")]
    [Arguments(429, "Wed, 21 Oct 2026 07:28:00 GMT", FailureCategory.ProviderRateLimited,
        "The provider rate-limited this request, retry after Wed, 21 Oct 2026 07:28:00 GMT.")]
    [Arguments(429, "30\r\nX-Injected: <script>", FailureCategory.ProviderRateLimited, "The provider rate-limited this request.")]
    [Arguments(500, null, FailureCategory.ProviderUnreachable, "The provider returned a server error (HTTP 500). Retry, or check the connection's gateway.")]
    [Arguments(503, "30", FailureCategory.ProviderUnreachable, "The provider returned a server error (HTTP 503). Retry, or check the connection's gateway.")]
    public void MapFailure_ProviderStatus_NamesCategoryWithFixedMessage(int status, string? retryAfter, FailureCategory expectedCategory, string expectedMessage)
    {
        var classification = InvocationFailureClassifier.MapFailure(FakeClientResultException.Create(status, retryAfter));

        AssertEx.Equal(expectedCategory, classification.Category);
        AssertEx.Equal(expectedMessage, classification.Message);
    }

    // A plain reason phrase is quoted; one carrying CR/LF, markup or too many characters is dropped, and the body never appears.
    [Test]
    [Arguments(500, "Internal Server Error", "HTTP 500 Internal Server Error")]
    [Arguments(502, "Bad Gateway (upstream_timeout)", "HTTP 502 Bad Gateway (upstream_timeout)")]
    [Arguments(500, "Internal Server Error\r\nX-Injected: yes", "HTTP 500")]
    [Arguments(503, "<script>alert(1)</script>", "HTTP 503")]
    [Arguments(504, "Gateway Timeout Gateway Timeout Gateway Timeout Gateway Timeout Gateway", "HTTP 504")]
    public void MapFailure_ServerErrorReasonPhrase_IsQuotedOnlyWhenPlain(int status, string reasonPhrase, string expectedStatusText)
    {
        var classification = InvocationFailureClassifier.MapFailure(FakeClientResultException.Create(status, retryAfter: null, reasonPhrase));

        AssertEx.Equal(FailureCategory.ProviderUnreachable, classification.Category);
        AssertEx.Equal($"The provider returned a server error ({expectedStatusText}). Retry, or check the connection's gateway.", classification.Message);
    }

    [Test]
    public void MapFailure_RateLimitWrappedByTheAgentFramework_WalksTheInnerChain()
    {
        var wrapped = new InvalidOperationException("agent run failed", FakeClientResultException.Create(429, "12"));

        var classification = InvocationFailureClassifier.MapFailure(wrapped);

        AssertEx.Equal(FailureCategory.ProviderRateLimited, classification.Category);
        AssertEx.Equal("The provider rate-limited this request, retry after 12.", classification.Message);
    }

    // R5-05: a work session step that reaches its cap is a designed stop, not a context-window overflow.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void MapFailure_ProviderCallBudget_IsTheCallBudgetCategoryWithItsOwnMessage(bool stepCap)
    {
        var message = stepCap ? ProviderCallBudget.StepCallCapReachedMessage : ProviderCallBudget.CeilingExceededMessage;

        var classification = InvocationFailureClassifier.MapFailure(new ProviderCallBudgetExceededException(message));

        AssertEx.Equal(FailureCategory.CallBudgetReached, classification.Category);
        AssertEx.Equal(message, classification.Message);
    }

    [Test]
    public void MapFailure_UnmappedStatus418_KeepsTheCatchAll()
    {
        var exception = FakeClientResultException.Create(418, retryAfter: null);

        var classification = InvocationFailureClassifier.MapFailure(exception);

        AssertEx.Equal(FailureCategory.Unexpected, classification.Category);
        AssertEx.Equal(exception.Message.Length > 512 ? exception.Message[..512] : exception.Message, classification.Message);
    }

    // A capacity refusal of the model's launch gets its own category and keeps its sanitized, actionable text verbatim.
    [Test]
    public void MapFailure_CapacityRefused_IsInsufficientCapacityWithTheRefusalVerbatim()
    {
        const string refusal = "Insufficient capacity for 'big' (Chat): another model is still loading. Loading now: 'other' (Chat).";

        var classification = InvocationFailureClassifier.MapFailure(new LlamaCapacityRefusedException(refusal));

        AssertEx.Equal(FailureCategory.InsufficientCapacity, classification.Category);
        AssertEx.Equal(refusal, classification.Message);
    }

    // A wrapper naming a model-load failure over a 500 stays ModelLoadFailed: the 5xx arm runs after the model-load arm.
    [Test]
    public void MapFailure_Status500UnderAModelLoadMessage_StaysModelLoadFailed()
    {
        var exception = new InvalidOperationException("unable to load model", FakeClientResultException.Create(500, retryAfter: null));

        var classification = InvocationFailureClassifier.MapFailure(exception);

        AssertEx.Equal(FailureCategory.ModelLoadFailed, classification.Category);
    }

    [Test]
    [Arguments(AzureFoundryProviderErrorKind.AuthFailed)]
    [Arguments(AzureFoundryProviderErrorKind.AuthRequired)]
    public void MapFailure_AzureAuthKinds_AreProviderAuthFailedWithTheTranslatorsMessage(AzureFoundryProviderErrorKind kind)
    {
        const string sanitized = "Azure Foundry rejected the Entra ID bearer token (sign in again, or check the token scope).";
        var exception = new AzureFoundryProviderException(kind, sanitized, FakeClientResultException.Create(401, retryAfter: null));

        var classification = InvocationFailureClassifier.MapFailure(exception);

        AssertEx.Equal(FailureCategory.ProviderAuthFailed, classification.Category);
        AssertEx.Equal(sanitized, classification.Message);
    }

    // Other Azure kinds keep their earlier landing even when the wrapped SDK exception carries a mapped status.
    [Test]
    [Arguments(AzureFoundryProviderErrorKind.Transport, 429)]
    [Arguments(AzureFoundryProviderErrorKind.ContentFiltered, 400)]
    [Arguments(AzureFoundryProviderErrorKind.Configuration, 404)]
    public void MapFailure_OtherAzureKinds_KeepTheCatchAll(AzureFoundryProviderErrorKind kind, int innerStatus)
    {
        const string message = "The Azure Foundry endpoint returned an error.";
        var exception = new AzureFoundryProviderException(kind, message, FakeClientResultException.Create(innerStatus, "5"));

        var classification = InvocationFailureClassifier.MapFailure(exception);

        AssertEx.Equal(FailureCategory.Unexpected, classification.Category);
        AssertEx.Equal(message, classification.Message);
    }

    // The agent framework wraps exceptions; an Azure exception anywhere in the chain keeps its own classification.
    [Test]
    public void MapFailure_WrappedAzureAuthFailed_KeepsTheAzureText()
    {
        const string sanitized = "Azure Foundry rejected the Entra ID bearer token (sign in again, or check the token scope).";
        var azure = new AzureFoundryProviderException(AzureFoundryProviderErrorKind.AuthFailed, sanitized, FakeClientResultException.Create(401, retryAfter: null));

        var classification = InvocationFailureClassifier.MapFailure(new InvalidOperationException("agent run failed", azure));

        AssertEx.Equal(FailureCategory.ProviderAuthFailed, classification.Category);
        AssertEx.Equal(sanitized, classification.Message);
    }

    [Test]
    public void MapFailure_WrappedAzureTransport404_KeepsTheCatchAll()
    {
        var azure = new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Transport, "The Azure Foundry endpoint returned an error (HTTP 404).",
            FakeClientResultException.Create(404, retryAfter: null));

        var classification = InvocationFailureClassifier.MapFailure(new InvalidOperationException("agent run failed", azure));

        AssertEx.Equal(FailureCategory.Unexpected, classification.Category);
        AssertEx.Equal("agent run failed", classification.Message);
    }

    [Test]
    public void MapFailure_UnrelatedException_KeepsTheCatchAll()
    {
        var classification = InvocationFailureClassifier.MapFailure(new ArgumentException("unrelated failure"));

        AssertEx.Equal(FailureCategory.Unexpected, classification.Category);
        AssertEx.Equal("unrelated failure", classification.Message);
    }

    // llama-server's context-size body (type plus counts) and an OpenAI-compatible one (code only), bare and wrapped by the agent framework.
    [Test]
    [Arguments(
        """{"error":{"code":400,"message":"request (9000 tokens) exceeds the available context size (8192 tokens), try increasing it","type":"exceed_context_size_error","n_prompt_tokens":9000,"n_ctx":8192}}""",
        false)]
    [Arguments("""{"error":{"message":"This model's maximum context length is 8192 tokens.","type":"invalid_request_error","param":"messages","code":"context_length_exceeded"}}""", true)]
    public void MapFailure_ProviderContextSizeRejection_IsContextWindowExceededWithTheFixedMessage(string body, bool wrapped)
    {
        Exception exception = FakeClientResultException.Create(400, retryAfter: null, "Bad Request", body);
        if (wrapped)
        {
            exception = new InvalidOperationException("agent run failed", exception);
        }

        var classification = InvocationFailureClassifier.MapFailure(exception);

        AssertEx.Equal(FailureCategory.ContextWindowExceeded, classification.Category);
        AssertEx.Equal(ProviderContextOverflow.Message, classification.Message);
    }

    // A 400 whose body is not a context-size rejection keeps its current landing: the grammar arm under a wrapper, the catch-all bare.
    [Test]
    public void MapFailure_Status400WithAnUnrelatedBody_KeepsItsCurrentArm()
    {
        const string body = """{"error":{"code":400,"message":"'messages' must not be empty","type":"invalid_request_error"}}""";
        var bare = FakeClientResultException.Create(400, retryAfter: null, "Bad Request", body);

        var grammar = InvocationFailureClassifier.MapFailure(new InvalidOperationException("failed to parse grammar", bare));
        var catchAll = InvocationFailureClassifier.MapFailure(bare);

        AssertEx.Equal(FailureCategory.ModelCapabilityUnsupported, grammar.Category);
        AssertEx.Equal(FailureCategory.Unexpected, catchAll.Category);
    }

    // The gateway samples: an operator's gateway answers both API surfaces with these bodies.
    [Test]
    [Arguments(500, """{"error":"unsupported_tpm_configuration","message":"TokensPerMinute must be one of 10000, 30000, 60000, 100000, 200000, 500000, 1000000.","configuredTokensPerMinute":80000}""",
        FailureCategory.ProviderUnreachable,
        ServerErrorMessage + " (unsupported_tpm_configuration: TokensPerMinute must be one of 10000, 30000, 60000, 100000, 200000, 500000, 1000000.)")]
    [Arguments(401, """{"error":"invalid_token","message":"Access token is expired or invalid."}""", FailureCategory.ProviderAuthFailed,
        TokenRejectedMessage + " (invalid_token: Access token is expired or invalid.)")]
    [Arguments(401, """{"error":{"message":"Incorrect API key provided.","type":"invalid_request_error","code":"invalid_api_key"}}""", FailureCategory.ProviderAuthFailed,
        AuthFailedMessage + " (invalid_api_key: Incorrect API key provided.)")]
    [Arguments(403, """{"code":"denied","message":"Denied by policy."}""", FailureCategory.ProviderAuthFailed, AuthFailedMessage + " (denied: Denied by policy.)")]
    [Arguments(500, "<html><body><h1>502 Bad Gateway</h1></body></html>", FailureCategory.ProviderUnreachable, ServerErrorMessage)]
    [Arguments(500, "", FailureCategory.ProviderUnreachable, ServerErrorMessage)]
    [Arguments(500, "[\"not an object\"]", FailureCategory.ProviderUnreachable, ServerErrorMessage)]
    [Arguments(500, """{"message":"line one\r\nline two"}""", FailureCategory.ProviderUnreachable, ServerErrorMessage + " (line one line two)")]
    [Arguments(500, """{"message":"bad\r\n<script>alert(1)</script>"}""", FailureCategory.ProviderUnreachable, ServerErrorMessage)]
    [Arguments(500, """{"error":"<b>code</b>","message":"Plain text."}""", FailureCategory.ProviderUnreachable, ServerErrorMessage + " (Plain text.)")]
    [Arguments(401, """{"message":"Invalid API key: sk-secret123"}""", FailureCategory.ProviderAuthFailed, AuthFailedMessage)]
    [Arguments(401, """{"error":"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl","message":"Token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl expired."}""",
        FailureCategory.ProviderAuthFailed, AuthFailedMessage)]
    [Arguments(500, """{"message":"Upstream https://gateway.internal.example/v1/chat failed."}""", FailureCategory.ProviderUnreachable, ServerErrorMessage)]
    [Arguments(500, """{"message":"Cannot open /var/lib/gateway/tenants.json now."}""", FailureCategory.ProviderUnreachable, ServerErrorMessage)]
    [Arguments(500, """{"code":"sk-live-abc","message":"Quota exceeded."}""", FailureCategory.ProviderUnreachable, ServerErrorMessage + " (Quota exceeded.)")]
    public void MapFailure_ProviderBody_AppendsOnlyTheSanitizedDetail(int status, string body, FailureCategory expectedCategory, string expectedMessage)
    {
        var classification = InvocationFailureClassifier.MapFailure(FakeClientResultException.Create(status, retryAfter: null, "Internal Server Error", body));

        AssertEx.Equal(expectedCategory, classification.Category);
        AssertEx.Equal(expectedMessage, classification.Message);
    }

    // The 2026-10-26 enterprise gateway samples: a wrong base path, the wrong API surface, backend RBAC, an expired token, and an
    // "unsupported operation" body whose status is not known yet (shown under 400 and 404). Other apim_policy_error faults stay plain server errors.
    [Test]
    [Arguments(500, "APIM policy error", """{"error":{"code":"apim_policy_error","message":"Unable to match incoming request to an operation."}}""",
        FailureCategory.ProviderUnreachable, NoRouteMessage + " (apim_policy_error: Unable to match incoming request to an operation.)")]
    [Arguments(500, "Internal Server Error", """{"error":{"code":"apim_policy_error","message":"Policy expression evaluation failed."}}""",
        FailureCategory.ProviderUnreachable, ServerErrorMessage + " (apim_policy_error: Policy expression evaluation failed.)")]
    [Arguments(404, "Not Found", """{"error":{"code":"api_not_supported","message":"Requested API is currently not supported","details":"See the gateway docs."}}""",
        FailureCategory.ModelUnavailable,
        "The provider does not offer the selected model on this API. Switch the model's API surface (Chat Completions or Responses) on the connection. (api_not_supported: Requested API is currently not supported)")]
    [Arguments(401, "Unauthorized", """{"error":{"code":"PermissionDenied","message":"Principal does not have access to API/Operation."}}""",
        FailureCategory.ModelUnavailable, BackendNotPermittedMessage + " (PermissionDenied)")]
    [Arguments(401, "Unauthorized",
        """{"error":{"code":"PermissionDenied","message":"The principal `00000000-1111-2222-3333-444444444444` lacks the required data action `Microsoft.CognitiveServices/accounts/OpenAI/responses/write` to perform `POST /openai/v1/responses` operation."}}""",
        FailureCategory.ModelUnavailable, BackendNotPermittedMessage + " (PermissionDenied)")]
    [Arguments(401, "Unauthorized", """{"error":{"code":"invalid_token","message":"Access token is expired or invalid."}}""",
        FailureCategory.ProviderAuthFailed, TokenRejectedMessage + " (invalid_token: Access token is expired or invalid.)")]
    [Arguments(400, "Bad Request", UnsupportedOperationBody,
        FailureCategory.Unexpected, "The provider rejected the request (HTTP 400). (The requested operation is unsupported.)")]
    [Arguments(404, "Not Found", UnsupportedOperationBody, FailureCategory.ModelUnavailable, ModelNotOfferedMessage + " (The requested operation is unsupported.)")]
    public void MapFailure_EnterpriseGatewaySamples_NameTheFixForEachShape(int status, string reasonPhrase, string body, FailureCategory expectedCategory, string expectedMessage)
    {
        var classification = InvocationFailureClassifier.MapFailure(new InvalidOperationException("agent run failed",
            FakeClientResultException.Create(status, retryAfter: null, reasonPhrase, body)));

        AssertEx.Equal(expectedCategory, classification.Category);
        AssertEx.Equal(expectedMessage, classification.Message);
    }

    // A 403 whose reason names the model sends the operator to the model id; any other 403 reason stays a credential failure.
    [Test]
    [Arguments("Model is not assigned to this project.", FailureCategory.ModelUnavailable,
        "The provider does not offer the selected model to this connection (Model is not assigned to this project.). Check the model id on the connection.")]
    [Arguments("Project does not exist.", FailureCategory.ProviderAuthFailed, AuthFailedMessage + " (Project does not exist.)")]
    [Arguments("Missing or invalid required header 'X-Model-Token'.", FailureCategory.ProviderAuthFailed,
        AuthFailedMessage + " (Missing or invalid required header 'X-Model-Token'.)")]
    [Arguments("Your model quota is exhausted.", FailureCategory.ProviderAuthFailed, AuthFailedMessage + " (Your model quota is exhausted.)")]
    public void MapFailure_PolicyGateway403_SplitsModelFromCredentialAndNeverSurfacesTheEchoedIds(string reason, FailureCategory expectedCategory, string expectedMessage)
    {
        var body = PolicyBodyTemplate.Replace("{0}", reason, StringComparison.Ordinal);

        var classification = InvocationFailureClassifier.MapFailure(new InvalidOperationException("agent run failed",
            FakeClientResultException.Create(403, retryAfter: null, body: body)));

        AssertEx.Equal(expectedCategory, classification.Category);
        AssertEx.Equal(expectedMessage, classification.Message);
        foreach (var id in (string[])[TenantId, UserId, ProjectId, "DeepSeek-V4-Flash-2"])
        {
            AssertEx.False(classification.Message.Contains(id, StringComparison.Ordinal), $"The message surfaced '{id}': {classification.Message}");
        }
    }

    // A message longer than the cap is cut to it; the cut is plain text because the filter ran first.
    [Test]
    public void MapFailure_LongBodyMessage_IsCappedAtThreeHundredCharacters()
    {
        var longMessage = string.Concat(Enumerable.Repeat("word ", 100));

        var classification = InvocationFailureClassifier.MapFailure(FakeClientResultException.Create(500, retryAfter: null, "Internal Server Error",
            $$"""{"message":"{{longMessage}}"}"""));

        AssertEx.Equal($"{ServerErrorMessage} ({longMessage[..300].TrimEnd()})", classification.Message);
    }

    /// <summary>R2-05: a model server that died mid-stream is named, with the resend that reloads it, never the raw framework text.</summary>
    [Test]
    public void MapFailure_ResponseEndedMidStream_NamesTheExitedServerAndTheResend()
    {
        var wrapped = new InvalidOperationException("stream failed",
            new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely. (ResponseEnded)"));

        var classification = InvocationFailureClassifier.MapFailure(wrapped);

        AssertEx.Equal(FailureCategory.ProviderUnreachable, classification.Category);
        AssertEx.Contains(classification.Message, "may have exited");
        AssertEx.Contains(classification.Message, "Send the message again");
        AssertEx.False(classification.Message.Contains("ResponseEnded", StringComparison.Ordinal), classification.Message);
    }
}
