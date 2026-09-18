namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription.Capture;

/// <summary>
///     Lists the processes that currently hold a render audio session, i.e. the ones per-application capture can
///     target. Operator-gated. No path, window title or command line is surfaced — only the process id and name.
/// </summary>
/// <remarks>
///     A host without WASAPI process loopback answers 200 with <c>supported: false</c> and an empty list rather than
///     404: the SPA hides the source entirely on such a node, and a 404 would be indistinguishable from a routing
///     mistake. An empty list on a supported host simply means nothing is playing.
/// </remarks>
public sealed class ListCaptureProcessesEndpoint : EndpointWithoutRequest<CaptureProcessListResponse>
{
    private readonly IProcessAudioCaptureSource _captureSource;

    public ListCaptureProcessesEndpoint(IProcessAudioCaptureSource captureSource)
    {
        ArgumentNullException.ThrowIfNull(captureSource);
        _captureSource = captureSource;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Transcription.CaptureProcesses);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<CaptureProcessListResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var candidates = await _captureSource.ListCandidatesAsync(ct);

        await Send.OkAsync(new CaptureProcessListResponse
        {
            Supported = _captureSource.IsSupported,
            Processes =
            [
                .. candidates.Select(static candidate => new CaptureProcessResponse
                {
                    Pid = candidate.ProcessId,
                    Name = candidate.Name,
                    HasAudio = candidate.HasAudio
                })
            ]
        }, ct);
    }
}
