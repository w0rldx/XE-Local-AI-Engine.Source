namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Workspace;

public interface IDevelopmentProfileBackfillService
{
    /// <summary>
    ///     Returns the project with a command profile attached, backfilling one if it is missing and the repository can
    ///     be reached. A project that already has a profile is returned untouched and does no work.
    /// </summary>
    Task<DevelopmentProjectSnapshot> EnsureAsync(DevelopmentProjectSnapshot project, CancellationToken cancellationToken = default);

    /// <summary>Backfills every profile-less project that can be reached. Returns how many were filled.</summary>
    Task<int> BackfillAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Fills the command profile on projects created before the profile existed.
/// </summary>
/// <remarks>
///     Detection is re-run against the project's own bound repository, and an unreachable one is left null rather
///     than defaulted to <c>generic-git</c>: that profile's gate is the whitespace check alone, so substituting it
///     would downgrade a real .NET project from "builds and tests pass" to "no trailing whitespace" and still report
///     green. The visible "re-register the repository" error stands instead. This runs at startup for every project
///     and again on project load, so a repository offline at boot needs no restart once it is back.
/// </remarks>
internal sealed class DevelopmentProfileBackfillService : IDevelopmentProfileBackfillService
{
    private readonly ILogger<DevelopmentProfileBackfillService> _logger;
    private readonly IDevelopmentCommandProfileDetector _profileDetector;
    private readonly IDevelopmentRepositoryBindingService _repositoryBindings;
    private readonly IDevelopmentStore _store;

    public DevelopmentProfileBackfillService(
        IDevelopmentStore store,
        IDevelopmentRepositoryBindingService repositoryBindings,
        IDevelopmentCommandProfileDetector profileDetector,
        ILogger<DevelopmentProfileBackfillService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(profileDetector);
        ArgumentNullException.ThrowIfNull(repositoryBindings);
        ArgumentNullException.ThrowIfNull(store);
        _logger = logger;
        _profileDetector = profileDetector;
        _repositoryBindings = repositoryBindings;
        _store = store;
    }

    public async Task<DevelopmentProjectSnapshot> EnsureAsync(DevelopmentProjectSnapshot project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!string.IsNullOrWhiteSpace(project.CommandProfileJson))
        {
            return project;
        }

        var profileJson = await DetectProfileJsonAsync(project, cancellationToken);
        if (profileJson is null)
        {
            return project;
        }

        try
        {
            return await _store.BackfillCommandProfileAsync(project.Id, profileJson, cancellationToken);
        }
        catch (DevelopmentConcurrencyException exception)
        {
            _logger.LogWarning(exception,
                "Development project {ProjectId} changed while its command profile was being backfilled.",
                project.Id);
            return project;
        }
    }

    public async Task<int> BackfillAllAsync(CancellationToken cancellationToken = default)
    {
        var projects = await _store.ListProjectsAsync(cancellationToken);
        var filled = 0;
        foreach (var project in projects.Where(static candidate => string.IsNullOrWhiteSpace(candidate.CommandProfileJson)))
        {
            var updated = await EnsureAsync(project, cancellationToken);
            if (!string.IsNullOrWhiteSpace(updated.CommandProfileJson))
            {
                filled++;
            }
        }

        return filled;
    }

    /// <summary>
    ///     Runs detection against the project's bound repository and materializes the canonical profile bytes, or
    ///     returns null when the repository is unreachable or the detected target will not materialize.
    /// </summary>
    /// <remarks>Every null path is a deliberate "leave it null", never a fallback profile.</remarks>
    private async Task<string?> DetectProfileJsonAsync(DevelopmentProjectSnapshot project, CancellationToken cancellationToken)
    {
        try
        {
            var repository = await _repositoryBindings.ResolveProjectAsync(project.Id, cancellationToken);
            var detected = _profileDetector.Detect(repository.RepositoryRoot);
            var profile = DevelopmentCommandProfileCatalog.Materialize(detected.ProfileId, detected.BuildTarget);
            return Encoding.UTF8.GetString(profile.ToCanonicalUtf8());
        }
        catch (Exception exception) when (exception is DevelopmentWorkspaceSecurityException
                                              or SelectedFolderValidationException
                                              or KeyNotFoundException
                                              or DirectoryNotFoundException
                                              or IOException
                                              or UnauthorizedAccessException)
        {
            _logger.LogInformation(exception,
                "Development project {ProjectId} has no command profile and its repository could not be inspected; leaving it unset.",
                project.Id);
            return null;
        }
    }
}
