namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     The poll route for a running or last-finished source build: its phase, a bounded sanitized log tail, and the
///     descriptor of what is being built. Operator-gated.
/// </summary>
public sealed class GetWhisperCppSourceBuildStatusEndpoint(IWhisperCppSourceBuildService buildService)
    : EndpointWithoutRequest<WhisperCppSourceBuildStatusResponse>
{
    private readonly IWhisperCppSourceBuildService _buildService = buildService ?? throw new ArgumentNullException(nameof(buildService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Transcription.RuntimeSourceBuildStatus);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<WhisperCppSourceBuildStatusResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(_buildService.GetStatus().ToResponse(), ct).ConfigureAwait(false);
    }
}
