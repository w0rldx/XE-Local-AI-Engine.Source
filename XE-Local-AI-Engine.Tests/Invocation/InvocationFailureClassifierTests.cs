namespace XE_Local_AI_Engine.Tests.Invocation;

using System.ClientModel;
using System.ClientModel.Primitives;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins how <see cref="InvocationFailureClassifier.MapFailure" /> names an OpenAI-compatible provider or gateway
///     rejection (<see cref="ClientResultException" /> by status) and an Azure auth failure, without leaking the body.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class InvocationFailureClassifierTests
{
    private const string AuthFailedMessage = "The provider rejected the credentials. Replace the API key or bearer token in the connection settings.";

    [Test]
    [Arguments(401, null, FailureCategory.ProviderAuthFailed, AuthFailedMessage)]
    [Arguments(403, null, FailureCategory.ProviderAuthFailed, AuthFailedMessage)]
    [Arguments(404, null, FailureCategory.ModelUnavailable, "The provider does not offer the selected model. Check the model id on the connection.")]
    [Arguments(429, null, FailureCategory.ProviderRateLimited, "The provider rate-limited this request.")]
    [Arguments(429, "30", FailureCategory.ProviderRateLimited, "The provider rate-limited this request, retry after 30.")]
    [Arguments(429, "Wed, 21 Oct 2026 07:28:00 GMT", FailureCategory.ProviderRateLimited,
        "The provider rate-limited this request, retry after Wed, 21 Oct 2026 07:28:00 GMT.")]
    [Arguments(429, "30\r\nX-Injected: <script>", FailureCategory.ProviderRateLimited, "The provider rate-limited this request.")]
    [Arguments(500, null, FailureCategory.ProviderUnreachable, "Provider unreachable.")]
    [Arguments(503, "30", FailureCategory.ProviderUnreachable, "Provider unreachable.")]
    public void MapFailure_ProviderStatus_NamesCategoryWithFixedMessage(int status, string? retryAfter, FailureCategory expectedCategory, string expectedMessage)
    {
        var classification = InvocationFailureClassifier.MapFailure(CreateClientResultException(status, retryAfter));

        AssertEx.Equal(expectedCategory, classification.Category);
        AssertEx.Equal(expectedMessage, classification.Message);
    }

    [Test]
    public void MapFailure_RateLimitWrappedByTheAgentFramework_WalksTheInnerChain()
    {
        var wrapped = new InvalidOperationException("agent run failed", CreateClientResultException(429, "12"));

        var classification = InvocationFailureClassifier.MapFailure(wrapped);

        AssertEx.Equal(FailureCategory.ProviderRateLimited, classification.Category);
        AssertEx.Equal("The provider rate-limited this request, retry after 12.", classification.Message);
    }

    [Test]
    public void MapFailure_UnmappedStatus418_KeepsTheCatchAll()
    {
        var exception = CreateClientResultException(418, retryAfter: null);

        var classification = InvocationFailureClassifier.MapFailure(exception);

        AssertEx.Equal(FailureCategory.Unexpected, classification.Category);
        AssertEx.Equal(exception.Message.Length > 512 ? exception.Message[..512] : exception.Message, classification.Message);
    }

    // A wrapper naming a model-load failure over a 500 stays ModelLoadFailed: the 5xx arm runs after the model-load arm.
    [Test]
    public void MapFailure_Status500UnderAModelLoadMessage_StaysModelLoadFailed()
    {
        var exception = new InvalidOperationException("unable to load model", CreateClientResultException(500, retryAfter: null));

        var classification = InvocationFailureClassifier.MapFailure(exception);

        AssertEx.Equal(FailureCategory.ModelLoadFailed, classification.Category);
    }

    [Test]
    [Arguments(AzureFoundryProviderErrorKind.AuthFailed)]
    [Arguments(AzureFoundryProviderErrorKind.AuthRequired)]
    public void MapFailure_AzureAuthKinds_AreProviderAuthFailedWithTheTranslatorsMessage(AzureFoundryProviderErrorKind kind)
    {
        const string sanitized = "Azure Foundry rejected the Entra ID bearer token (sign in again, or check the token scope).";
        var exception = new AzureFoundryProviderException(kind, sanitized, CreateClientResultException(401, retryAfter: null));

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
        var exception = new AzureFoundryProviderException(kind, message, CreateClientResultException(innerStatus, "5"));

        var classification = InvocationFailureClassifier.MapFailure(exception);

        AssertEx.Equal(FailureCategory.Unexpected, classification.Category);
        AssertEx.Equal(message, classification.Message);
    }

    // The agent framework wraps exceptions; an Azure exception anywhere in the chain keeps its own classification.
    [Test]
    public void MapFailure_WrappedAzureAuthFailed_KeepsTheAzureText()
    {
        const string sanitized = "Azure Foundry rejected the Entra ID bearer token (sign in again, or check the token scope).";
        var azure = new AzureFoundryProviderException(AzureFoundryProviderErrorKind.AuthFailed, sanitized, CreateClientResultException(401, retryAfter: null));

        var classification = InvocationFailureClassifier.MapFailure(new InvalidOperationException("agent run failed", azure));

        AssertEx.Equal(FailureCategory.ProviderAuthFailed, classification.Category);
        AssertEx.Equal(sanitized, classification.Message);
    }

    [Test]
    public void MapFailure_WrappedAzureTransport404_KeepsTheCatchAll()
    {
        var azure = new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Transport, "The Azure Foundry endpoint returned an error (HTTP 404).",
            CreateClientResultException(404, retryAfter: null));

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

    private static ClientResultException CreateClientResultException(int status, string? retryAfter)
    {
        // The fake owns nothing (its Dispose is empty), so the exception may keep reading it after this scope.
        using var response = new FakePipelineResponse(status, BinaryData.FromString("""{"error":{"message":"body-sentinel"}}"""), retryAfter);
        return new ClientResultException(response, innerException: null);
    }

    // A PipelineResponse with a fixed status, JSON body and optional Retry-After header, so GetRawResponse() is the real read path.
    private sealed class FakePipelineResponse : PipelineResponse
    {
        private readonly FakeHeaders _headers;

        public FakePipelineResponse(int status, BinaryData content, string? retryAfter)
        {
            Status = status;
            Content = content;
            _headers = new FakeHeaders(retryAfter);
        }

        public override int Status { get; }

        public override string ReasonPhrase => string.Empty;

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content { get; }

        protected override PipelineResponseHeaders HeadersCore => _headers;

        public override BinaryData BufferContent(CancellationToken cancellationToken = default)
        {
            return Content;
        }

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(Content);
        }

        public override void Dispose()
        {
        }
    }

    private sealed class FakeHeaders : PipelineResponseHeaders
    {
        private readonly List<KeyValuePair<string, string>> _headers;

        public FakeHeaders(string? retryAfter)
        {
            _headers = retryAfter is null ? [] : [new KeyValuePair<string, string>("Retry-After", retryAfter)];
        }

        public override bool TryGetValue(string name, out string? value)
        {
            value = _headers.FirstOrDefault(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            return value is not null;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            var found = _headers.Where(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Select(static header => header.Value).ToList();
            values = found.Count == 0 ? null : found;
            return found.Count > 0;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            return _headers.GetEnumerator();
        }
    }
}
