namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>Read-only release notes for the running and newer versions, from the feed the last update check read.</summary>
/// <remarks>
///     Never starts a GitHub request: it reads the cached <see cref="IAppUpdateState" /> snapshot, so the status
///     endpoint's 10-minute refresh floor stays the only way a check runs.
/// </remarks>
public sealed class GetAppReleaseNotesEndpoint : EndpointWithoutRequest<AppReleaseNotesResponse>, IDesktopOnlyEndpoint
{
    private readonly IAppUpdateService _updateService;

    public GetAppReleaseNotesEndpoint(IAppUpdateService updateService)
    {
        ArgumentNullException.ThrowIfNull(updateService);
        _updateService = updateService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.AppUpdate.ReleaseNotes);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(ToResponse(await _updateService.GetStatusAsync(ct)), ct);
    }

    internal static AppReleaseNotesResponse ToResponse(AppUpdateSnapshot snapshot)
    {
        return new AppReleaseNotesResponse
        {
            CurrentVersion = snapshot.CurrentVersion,
            Items =
            [
                .. snapshot.ReleaseNotes.Select(static note => new AppReleaseNoteResponse
                {
                    Version = note.Version,
                    NotesMarkdown = note.NotesMarkdown,
                    IsTruncated = note.IsTruncated,
                    IsNewerThanRunning = note.IsNewerThanRunning
                })
            ]
        };
    }
}
