namespace XE_Local_AI_Engine.Client.Endpoints.Voice.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Voice;

public sealed class GetVoiceManifestEndpoint(IVoiceManifestService voiceManifestService) : EndpointWithoutRequest<VoiceManifestResponse>
{
    private readonly IVoiceManifestService _voiceManifestService = voiceManifestService ?? throw new ArgumentNullException(nameof(voiceManifestService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Voice.Manifest);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var manifest = await _voiceManifestService.GetManifestAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(manifest.ToResponse(), ct).ConfigureAwait(false);
    }
}
