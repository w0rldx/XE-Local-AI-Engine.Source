namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.ClientModel.Primitives;

/// <summary>
///     Sets the Azure OpenAI v1 API-surface <c>api-key</c> header on every outbound request. The v1 surface is
///     reached through the plain OpenAI SDK client, whose own credential-based policy sets
///     <c>Authorization: Bearer &lt;key&gt;</c> instead — the wrong header for a gateway that validates
///     <c>api-key</c> (Locked v1 surface contract). This policy carries the real key on the header the gateway
///     expects; the SDK's own Authorization header (carrying an unused placeholder key, see
///     <see cref="AzureFoundryChatClientFactory" />) is harmless noise. <c>api-key</c> cannot be set via
///     <see cref="CustomHeaderPipelinePolicy" /> — it is a reserved custom-header name (Locked #8) — because it
///     carries auth semantics here, not because it is unsafe for this policy to set. Registered at
///     <see cref="PipelinePosition.PerCall" />, same as the other credential policies. No I/O and no logging — the
///     key must never be logged.
/// </summary>
internal sealed class ApiKeyHeaderPipelinePolicy : PipelinePolicy
{
    private const string ApiKeyHeaderName = "api-key";

    private readonly string _apiKey;

    public ApiKeyHeaderPipelinePolicy(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _apiKey = apiKey;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ApplyHeader(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ApplyHeader(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private void ApplyHeader(PipelineMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        message.Request.Headers.Set(ApiKeyHeaderName, _apiKey);
    }
}
