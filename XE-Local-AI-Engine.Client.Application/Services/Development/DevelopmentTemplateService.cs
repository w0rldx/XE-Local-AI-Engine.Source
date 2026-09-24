namespace XE_Local_AI_Engine.Client.Services.Development;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Public so tests can substitute it, matching the other Development seams: this assembly is not strong-named, so
///     Castle DynamicProxy cannot proxy an internal interface.
/// </summary>
public interface IDevelopmentTemplateService
{
    Task<IReadOnlyList<DevelopmentTemplateReference>> ListTemplatesAsync(CancellationToken cancellationToken = default);

    Task<DevelopmentTemplateReference> AddTemplateAsync(string templateAlias, string hostPath, CancellationToken cancellationToken = default);

    Task<bool> RemoveTemplateAsync(Guid templateId, CancellationToken cancellationToken = default);

    Task<DevelopmentTemplateMaterializationResult> CreateFromTemplateAsync(Guid templateId,
        string destinationPath,
        string repositoryAlias,
        string baseBranch,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Creates a new Development repository from a template the operator already has on this host.
/// </summary>
/// <remarks>
///     A template is an ordinary Git repository, never a scaffolding engine: <c>clone</c> → drop <c>.git</c> →
///     <c>init</c> → one initial commit, with no token substitution or renaming, and any substitution added later
///     runs here in the engine before the initial commit, never as the agent's first task. <c>git worktree</c> is not
///     an option, because a worktree shares the template's object store, which would make every managed worktree a
///     child of the template's repository and leave an inherited <c>origin</c> a stray push can land in.
/// </remarks>
internal sealed class DevelopmentTemplateService : IDevelopmentTemplateService
{
    private readonly INodeDataDirectory _dataDirectory;
    private readonly DevelopmentOptions _options;
    private readonly IDevelopmentRepositoryBindingService _repositoryBindings;
    private readonly IDevelopmentTemplateStore _templateStore;
    private readonly TimeProvider _timeProvider;

    public DevelopmentTemplateService(IDevelopmentTemplateStore templateStore,
        IDevelopmentRepositoryBindingService repositoryBindings,
        INodeDataDirectory dataDirectory,
        IOptions<DevelopmentOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        _dataDirectory = dataDirectory;
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        ArgumentNullException.ThrowIfNull(repositoryBindings);
        _repositoryBindings = repositoryBindings;
        ArgumentNullException.ThrowIfNull(templateStore);
        _templateStore = templateStore;
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<DevelopmentTemplateReference>> ListTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var templates = await _templateStore.ListAsync(cancellationToken);
        var references = new List<DevelopmentTemplateReference>(templates.Count);
        foreach (var template in templates)
        {
            // A template lives on the host and can be moved or deleted behind the registry's back, so availability is
            // probed rather than assumed — the same treatment registered repositories get.
            var availability = await ProbeAvailabilityAsync(template.HostPath, cancellationToken);
            references.Add(new DevelopmentTemplateReference
            {
                Id = template.Id.ToString(),
                Alias = template.Alias,
                Availability = availability
            });
        }

        return references;
    }

    public async Task<DevelopmentTemplateReference> AddTemplateAsync(string templateAlias,
        string hostPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateAlias);
        var canonical = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(hostPath);
        await EnsureGitTopLevelAsync(canonical, cancellationToken);
        var template = await _templateStore.AddAsync(templateAlias.Trim(), canonical, cancellationToken);
        return new DevelopmentTemplateReference
        {
            Id = template.Id.ToString(),
            Alias = template.Alias,
            Availability = "Available"
        };
    }

    public Task<bool> RemoveTemplateAsync(Guid templateId, CancellationToken cancellationToken = default) =>
        _templateStore.RemoveAsync(templateId, cancellationToken);

    public async Task<DevelopmentTemplateMaterializationResult> CreateFromTemplateAsync(Guid templateId,
        string destinationPath,
        string repositoryAlias,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryAlias);
        EnsureSafeBranch(baseBranch);

        var template = await _templateStore.GetAsync(templateId, cancellationToken);
        var templateRoot = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(template.HostPath);
        await EnsureGitTopLevelAsync(templateRoot, cancellationToken);

        var destination = ResolveDestination(destinationPath, templateRoot);
        var templateCommit = await ResolveTemplateHeadAsync(templateRoot, cancellationToken);

        var created = false;
        try
        {
            Directory.CreateDirectory(destination);
            created = true;
            await MaterializeAsync(templateRoot, destination, template.Alias, templateCommit, baseBranch, cancellationToken);

            // Registration makes the new folder bindable and re-runs the git-top-level check every registered
            // repository passes, so a materialization that is not a canonical root fails here, not at the attempt.
            var repository = await _repositoryBindings.RegisterAsync(repositoryAlias, destination, cancellationToken);
            await _templateStore.RecordMaterializationAsync(new DevelopmentTemplateMaterializationSnapshot
                {
                    SelectedFolderId = Guid.Parse(repository.Id),
                    TemplateId = template.Id,
                    TemplateAlias = template.Alias,
                    TemplatePath = templateRoot,
                    TemplateCommit = templateCommit,
                    CreatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                },
                cancellationToken);
            return new DevelopmentTemplateMaterializationResult
            {
                Repository = repository,
                TemplateAlias = template.Alias,
                TemplateCommit = templateCommit
            };
        }
        catch
        {
            // A half-materialized directory is worse than none: it would register as a repository on a retry and carry
            // whatever the failed clone left behind. Only remove what this call created.
            if (created)
            {
                StandaloneGitClone.TryDelete(destination);
            }

            throw;
        }
    }

    /// <summary>
    ///     The materialization sequence. Every step is a separate bounded process and a non-zero exit stops the sequence, so a
    ///     partially cloned tree never reaches <c>git init</c>.
    /// </summary>
    private async Task MaterializeAsync(string templateRoot,
        string destination,
        string templateAlias,
        string templateCommit,
        string baseBranch,
        CancellationToken cancellationToken)
    {
        var git = new HostGitRunner(_options.TemplateMaterializationTimeoutSeconds);
        var parent = Path.GetDirectoryName(destination)
                     ?? throw new DevelopmentWorkspaceSecurityException("The destination path has no parent directory.");

        // Transport, flags and the standalone assertion are shared with the managed workspace via StandaloneGitClone.
        // file:// is mandatory: on a plain local path git ignores --depth and hardlinks the whole object store.
        var clone = await git.RunAsync(parent,
            AgentHomeGit.Arguments([.. StandaloneGitClone.Arguments(templateRoot, destination)]),
            cancellationToken);
        EnsureGitSuccess(clone, "The template could not be cloned.");

        if (!StandaloneGitClone.IsStandalone(destination))
        {
            throw new DevelopmentWorkspaceSecurityException("The template clone did not produce a standalone Git directory.");
        }

        // Dropping .git severs the template: the inherited origin, its history and any shared object state go with it.
        // The managed workspace must not do this — it detaches onto the base commit, or its patch cannot apply.
        StandaloneGitClone.Delete(Path.Combine(destination, ".git"));

        var init = await git.RunAsync(destination,
            AgentHomeGit.Arguments("init", "--initial-branch", baseBranch),
            cancellationToken);
        EnsureGitSuccess(init, "The materialized repository could not be initialized.");

        var add = await git.RunAsync(destination,
            AgentHomeGit.Arguments("add", "-A", "--", "."),
            cancellationToken);
        EnsureGitSuccess(add, "The materialized repository contents could not be staged.");

        // Identity is supplied per command rather than written into the new repository's config: the operator's own
        // may be unset here, and a commit failing for that leaves a HEADless repository that breaks far from the cause.
        var commit = await git.RunAsync(destination,
            AgentHomeGit.Arguments("-c", $"user.name={CommitAuthorName}",
                "-c", $"user.email={CommitAuthorEmail}",
                "commit", "--no-gpg-sign",
                "-m", $"Initial commit from template {templateAlias} @ {templateCommit}"),
            cancellationToken);
        EnsureGitSuccess(commit, "The materialized repository initial commit could not be created.");
    }

    /// <summary>
    ///     Resolves and validates the operator-chosen destination. It must be a fresh directory outside node data and
    ///     outside the template.
    /// </summary>
    private string ResolveDestination(string destinationPath, string templateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (destinationPath.Any(char.IsControl) || !Path.IsPathFullyQualified(destinationPath))
        {
            throw new DevelopmentWorkspaceSecurityException("The destination must be an absolute path.");
        }

        if (destinationPath.StartsWith("//", StringComparison.Ordinal) || destinationPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new DevelopmentWorkspaceSecurityException("Network destination paths are not supported for Development execution.");
        }

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationPath));
        if (destination.Split(Path.DirectorySeparatorChar).Any(segment => segment is "." or ".."))
        {
            throw new DevelopmentWorkspaceSecurityException("The destination path must not contain relative segments.");
        }

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new DevelopmentWorkspaceSecurityException("The destination directory already exists and is not empty.");
        }

        if (File.Exists(destination))
        {
            throw new DevelopmentWorkspaceSecurityException("The destination path is an existing file.");
        }

        // A project created by the user is a user artifact. Node data is where the engine's own managed
        // worktrees and runtime state live, and it is deleted and rebuilt on the engine's terms.
        if (IsWithin(destination, Path.TrimEndingDirectorySeparator(Path.GetFullPath(_dataDirectory.Root))))
        {
            throw new DevelopmentWorkspaceSecurityException("A Development project created from a template cannot live inside the node data directory.");
        }

        if (IsWithin(destination, templateRoot) || IsWithin(templateRoot, destination))
        {
            throw new DevelopmentWorkspaceSecurityException("The destination must not be inside the template, or contain it.");
        }

        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new DevelopmentWorkspaceSecurityException("The destination's parent directory does not exist.");
        }

        return destination;
    }

    private async Task<string> ResolveTemplateHeadAsync(string templateRoot, CancellationToken cancellationToken)
    {
        var git = new HostGitRunner(_options.TemplateMaterializationTimeoutSeconds);
        var head = await git.RunAsync(templateRoot,
            AgentHomeGit.Arguments("rev-parse", "--verify", "HEAD^{commit}"),
            cancellationToken);
        EnsureGitSuccess(head, "The template has no resolvable HEAD commit.");
        return head.StandardOutput.Trim();
    }

    private async Task<string> ProbeAvailabilityAsync(string hostPath, CancellationToken cancellationToken)
    {
        try
        {
            var canonical = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(hostPath);
            await EnsureGitTopLevelAsync(canonical, cancellationToken);
            return "Available";
        }
        catch (Exception exception) when (exception is DevelopmentWorkspaceSecurityException
                                              or DirectoryNotFoundException
                                              or IOException
                                              or UnauthorizedAccessException)
        {
            return "Unavailable";
        }
    }

    private async Task EnsureGitTopLevelAsync(string canonicalRoot, CancellationToken cancellationToken)
    {
        var git = new HostGitRunner(_options.TemplateMaterializationTimeoutSeconds);
        var result = await git.RunAsync(canonicalRoot,
            AgentHomeGit.Arguments("rev-parse", "--show-toplevel"),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new DevelopmentWorkspaceSecurityException("The template must be a Git repository root.");
        }

        var topLevel = Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.StandardOutput.Trim()));
        if (!PathEquals(topLevel, canonicalRoot))
        {
            throw new DevelopmentWorkspaceSecurityException("The template must be the canonical Git repository root.");
        }
    }

    /// <summary>
    ///     Mirrors <c>DevelopmentWorkspaceProvider.ValidateBaseBranch</c>: the branch name becomes a literal argument
    ///     and later has to resolve as <c>refs/heads/{branch}</c> when the managed worktree is created.
    /// </summary>
    private static void EnsureSafeBranch(string baseBranch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        if (baseBranch[0] == '-'
            || baseBranch.Contains("..", StringComparison.Ordinal)
            || baseBranch.Contains("@{", StringComparison.Ordinal)
            || baseBranch.Any(char.IsControl)
            || baseBranch.Any(char.IsWhiteSpace))
        {
            throw new DevelopmentWorkspaceSecurityException("The base branch is not a safe Git branch name.");
        }
    }

    private static bool IsWithin(string candidate, string root)
    {
        if (PathEquals(candidate, root))
        {
            return true;
        }

        var rooted = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rooted, PathComparison);
    }

    private static bool PathEquals(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            PathComparison);

    private static void EnsureGitSuccess(HostGitResult result, string message)
    {
        if (result.ExitCode != 0)
        {
            throw new DevelopmentTemplateMaterializationException(message);
        }
    }

    private const string CommitAuthorName = "XE Local AI Engine";
    private const string CommitAuthorEmail = "development@xe-local-ai-engine.invalid";

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
