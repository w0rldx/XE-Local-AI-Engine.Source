namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.ClientModel.Primitives;
using Azure.Core;

/// <summary>
///     A System.ClientModel pipeline policy that attaches a fresh Entra ID bearer token to every outbound Azure
///     Foundry / APIM gateway request. The underlying <see cref="TokenCredential" /> (client-secret / device-code /
///     interactive-browser) owns its own in-memory expiry cache, so this policy performs no caching of its own —
///     every call is a cheap cache-hit unless the token is near expiry. Never logs the token.
///     <para>
///         Derives from <see cref="AuthenticationPolicy" /> (not just <see cref="PipelinePolicy" />) so it can serve
///         two different roles depending on the wire surface:
///     </para>
///     <list type="bullet">
///         <item>
///             Azure deployments surface (<see cref="AzureOpenAIClient" />): registered at
///             <see cref="PipelinePosition.PerCall" /> (before retries) so a transient 401 retry re-fetches rather
///             than replaying a stale token. Composes with <see cref="CustomHeaderPipelinePolicy" />: both may be
///             registered at <see cref="PipelinePosition.PerCall" /> on the same client and each sets only its own
///             header name (<c>Authorization</c> is reserved and skipped by the custom-header policy, so the two
///             never race for the same header).
///         </item>
///         <item>
///             OpenAI-compatible v1 surface (plain <see cref="OpenAIClient" />): passed directly as the SDK's own
///             <see cref="AuthenticationPolicy" /> via the <c>OpenAIClient(AuthenticationPolicy, OpenAIClientOptions)</c>
///             constructor instead of being added at <see cref="PipelinePosition.PerCall" />. That constructor puts
///             the supplied policy in <see cref="ClientPipeline" />'s FIXED per-try policy slot — which the SDK's
///             internal pipeline-assembly code places AFTER every PerCall policy (including a PerCall-registered
///             instance of this same class). A PerCall registration on this surface would therefore be silently
///             overwritten by the SDK's own placeholder-credential auth policy before the request left the process;
///             passing this policy as the ctor's <c>authenticationPolicy</c> argument instead makes IT the
///             last-writer, since there is nothing left in the fixed slot to overwrite it. See
///             <see cref="AzureFoundryChatClientFactory" />'s v1 builders for the call sites.
///         </item>
///     </list>
/// </summary>
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

        var token = await _credential.GetTokenAsync(_requestContext, message.CancellationToken).ConfigureAwait(false);
        ApplyToken(message, token.Token);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private static void ApplyToken(PipelineMessage message, string accessToken)
    {
        message.Request.Headers.Set(AuthorizationHeaderName, $"{BearerScheme} {accessToken}");
    }
}
