namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.ClientModel.Primitives;
using Azure.Core;

/// <summary>
///     A System.ClientModel pipeline policy that attaches a fresh Entra ID bearer token to every outbound Azure
///     Foundry / APIM gateway request. Never logs the token.
/// </summary>
/// <remarks>
///     The underlying <see cref="TokenCredential" /> owns its own expiry cache, so this policy caches nothing. It derives from
///     <see cref="AuthenticationPolicy" />, not <see cref="PipelinePolicy" />, because the two wire surfaces install it differently: the Azure
///     deployments surface registers it at <see cref="PipelinePosition.PerCall" />, while the OpenAI-compatible v1 surface MUST pass it as the
///     <c>OpenAIClient(AuthenticationPolicy, OpenAIClientOptions)</c> ctor argument — a PerCall registration there is silently overwritten.
///     Why, and the call sites: docs/wiki/03-local-runtime-and-providers.md "Azure Foundry: the two wire surfaces".
/// </remarks>
internal sealed class EntraBearerTokenPipelinePolicy : AuthenticationPolicy
{
    private const string AuthorizationHeaderName = "Authorization";
    private const string BearerScheme = "Bearer";

    private readonly TokenCredential _credential;
    private readonly TokenRequestContext _requestContext;

    public EntraBearerTokenPipelinePolicy(TokenCredential credential, string scope)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        _credential = credential;
        _requestContext = new TokenRequestContext([scope]);
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ArgumentNullException.ThrowIfNull(message);

        var token = _credential.GetToken(_requestContext, message.CancellationToken);
        ApplyToken(message, token.Token);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ArgumentNullException.ThrowIfNull(message);

        var token = await _credential.GetTokenAsync(_requestContext, message.CancellationToken);
        ApplyToken(message, token.Token);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void ApplyToken(PipelineMessage message, string accessToken)
    {
        message.Request.Headers.Set(AuthorizationHeaderName, $"{BearerScheme} {accessToken}");
    }
}
