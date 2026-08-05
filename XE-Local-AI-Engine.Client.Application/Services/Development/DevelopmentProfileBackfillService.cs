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
///     <para>
///         Detection is re-run against the project's own bound repository, so a repository that has a solution gets the
///         .NET profile it would have got at registration. It is deliberately <em>not</em> defaulted to
///         <c>generic-git</c> when the repository cannot be reached: that profile's validation gate is the whitespace
///         check alone, so substituting it for a repository nobody could look at would silently downgrade a real .NET
///         project's gate from "builds and tests pass" to "no trailing whitespace" while still reporting a green
///         validation. An unreachable repository is therefore left null, and the existing
///         "re-register the repository" error stands — a visible failure rather than a quiet lie.
///     </para>
///     <para>
///         Runs both at startup (every project at once) and on project load, because a repository that was offline at
///         boot must not need an application restart to become usable once it is back.
///     </para>
/// </summary>
internal sealed class DevelopmentProfileBackfillService(
    IDevelopmentStore store,
    IDevelopmentRepositoryBindingService repositoryBindings,
    IDevelopmentCommandProfileDetector profileDetector,
    ILogger<DevelopmentProfileBackfillService> logger) : IDevelopmentProfileBackfillService
{
    private readonly ILogger<DevelopmentProfileBackfillService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IDevelopmentCommandProfileDetector _profileDetector = profileDetector ?? throw new ArgumentNullException(nameof(profileDetector));
    private readonly IDevelopmentRepositoryBindingService _repositoryBindings = repositoryBindings ?? throw new ArgumentNullException(nameof(repositoryBindings));
    private readonly IDevelopmentStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<DevelopmentProjectSnapshot> EnsureAsync(DevelopmentProjectSnapshot project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!string.IsNullOrWhiteSpace(project.CommandProfileJson))
        {
            return project;
        }

        var profileJson = await DetectProfileJsonAsync(project, cancellationToken).ConfigureAwait(false);
        if (profileJson is null)
        {
            return project;
        }

        try
        {
            return await _store.BackfillCommandProfileAsync(project.Id, profileJson, cancellationToken).ConfigureAwait(false);
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
        var projects = await _store.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
        var filled = 0;
        foreach (var project in projects.Where(static candidate => string.IsNullOrWhiteSpace(candidate.CommandProfileJson)))
        {
            var updated = await EnsureAsync(project, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(updated.CommandProfileJson))
            {
                filled++;
            }
        }

        return filled;
    }

    /// <summary>
    ///     Runs detection against the project's bound repository and materializes the canonical profile bytes, or returns
    ///     null when the repository cannot be reached or the detected target will not materialize. Every null path is a
    ///     deliberate "leave it null", never a fallback profile.
    /// </summary>
    private async Task<string?> DetectProfileJsonAsync(DevelopmentProjectSnapshot project, CancellationToken cancellationToken)
    {
        try
        {
            var repository = await _repositoryBindings.ResolveProjectAsync(project.Id, cancellationToken).ConfigureAwait(false);
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
