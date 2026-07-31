namespace XE_Local_AI_Engine.Client.Services.Development;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>A registered template as the API sees it: never a host path.</summary>
public sealed record DevelopmentTemplateReference(string Id, string Alias, string Availability);

/// <summary>
///     The result of materializing a template: the repository reference the project form will bind to, plus the
///     template commit that produced it.
/// </summary>
public sealed record DevelopmentTemplateMaterializationResult(
    DevelopmentRepositoryReference Repository,
    string TemplateAlias,
    string TemplateCommit);

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
///     <para>
///         A template is an ordinary Git repository, not a scaffolding engine, and this is deliberately what keeps the
///         feature small: <c>clone</c> → drop <c>.git</c> → <c>init</c> → one initial commit. There is no token
///         substitution and no renaming in v1 — a project cloned from <c>XE-Framework</c> is named XE-Framework until
///         the operator renames it. If substitution is ever added it runs here, in the engine, before the initial
///         commit, and never as the agent's first task.
///     </para>
///     <para>
///         <c>git worktree</c> is not an option for this. A worktree shares the template's object store, so its
///         <c>.git</c> is a pointer file and <c>--git-common-dir</c> resolves into the template — which makes the
///         managed worktree the engine later creates a child of the <em>template's</em> repository, so deleting or
///         rebasing the template breaks every project made from it, and the inherited <c>origin</c> means a stray push
///         lands in the template. Dropping <c>.git</c> removes the remote and the template's history along with it.
///     </para>
/// </summary>
internal sealed class DevelopmentTemplateService(
    IDevelopmentTemplateStore templateStore,
    IDevelopmentRepositoryBindingService repositoryBindings,
    INodeDataDirectory dataDirectory,
    IOptions<DevelopmentOptions> options,
    TimeProvider timeProvider) : IDevelopmentTemplateService
{
    private readonly INodeDataDirectory _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
    private readonly DevelopmentOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly IDevelopmentRepositoryBindingService _repositoryBindings = repositoryBindings ?? throw new ArgumentNullException(nameof(repositoryBindings));
    private readonly IDevelopmentTemplateStore _templateStore = templateStore ?? throw new ArgumentNullException(nameof(templateStore));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<IReadOnlyList<DevelopmentTemplateReference>> ListTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var templates = await _templateStore.ListAsync(cancellationToken).ConfigureAwait(false);
        var references = new List<DevelopmentTemplateReference>(templates.Count);
        foreach (var template in templates)
        {
            // A template lives on the host and can be moved or deleted behind the registry's back, so availability is
            // probed rather than assumed — the same treatment registered repositories get.
            var availability = await ProbeAvailabilityAsync(template.HostPath, cancellationToken).ConfigureAwait(false);
            references.Add(new DevelopmentTemplateReference(template.Id.ToString(), template.Alias, availability));
        }

        return references;
    }

    public async Task<DevelopmentTemplateReference> AddTemplateAsync(string templateAlias,
        string hostPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateAlias);
        var canonical = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(hostPath);
        await EnsureGitTopLevelAsync(canonical, cancellationToken).ConfigureAwait(false);
        var template = await _templateStore.AddAsync(templateAlias.Trim(), canonical, cancellationToken).ConfigureAwait(false);
        return new DevelopmentTemplateReference(template.Id.ToString(), template.Alias, "Available");
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

        var template = await _templateStore.GetAsync(templateId, cancellationToken).ConfigureAwait(false);
        var templateRoot = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(template.HostPath);
        await EnsureGitTopLevelAsync(templateRoot, cancellationToken).ConfigureAwait(false);

        var destination = ResolveDestination(destinationPath, templateRoot);
        var templateCommit = await ResolveTemplateHeadAsync(templateRoot, cancellationToken).ConfigureAwait(false);

        var created = false;
        try
        {
            Directory.CreateDirectory(destination);
            created = true;
            await MaterializeAsync(templateRoot, destination, template.Alias, templateCommit, baseBranch, cancellationToken).ConfigureAwait(false);

            // Registration is what makes the new folder a bindable Development repository, and it re-runs the same
            // git-top-level check every registered repository passes — so a materialization that produced something
            // that is not a canonical repository root fails here rather than at the first attempt.
            var repository = await _repositoryBindings.RegisterAsync(repositoryAlias, destination, cancellationToken).ConfigureAwait(false);
            await _templateStore.RecordMaterializationAsync(new DevelopmentTemplateMaterializationSnapshot(Guid.Parse(repository.Id),
                    template.Id,
                    template.Alias,
                    templateRoot,
                    templateCommit,
                    _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()),
                cancellationToken).ConfigureAwait(false);
            return new DevelopmentTemplateMaterializationResult(repository, template.Alias, templateCommit);
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
    ///     The S2.1 sequence. Every step is a separate bounded process and a non-zero exit stops the sequence, so a
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

        // The transport, the flag set and the standalone assertion are shared with the managed workspace (S3.4) via
        // StandaloneGitClone — including the reason file:// is mandatory: given a plain local path git SILENTLY IGNORES
        // --depth ("warning: --depth is ignored in local clones; use file:// instead") and hardlinks the whole object
        // store instead, which is exactly the shared-objects coupling this design exists to avoid.
        var clone = await git.RunAsync(parent,
            AgentHomeGit.Arguments([.. StandaloneGitClone.Arguments(templateRoot, destination)]),
            cancellationToken).ConfigureAwait(false);
        EnsureGitSuccess(clone, "The template could not be cloned.");

        if (!StandaloneGitClone.IsStandalone(destination))
        {
            throw new DevelopmentWorkspaceSecurityException("The template clone did not produce a standalone Git directory.");
        }

        // Where the two paths part company, and they must. Dropping .git is what severs the template: it removes the
        // inherited origin (so a stray push cannot land in the template), the template's history, and any shared object
        // state. The managed workspace deliberately does NOT do this — it keeps the cloned history and detaches onto
        // the recorded base commit, because a fabricated initial commit would make its patch unappliable by
        // construction.
        Directory.Delete(Path.Combine(destination, ".git"), recursive: true);

        var init = await git.RunAsync(destination,
            AgentHomeGit.Arguments("init", "--initial-branch", baseBranch),
            cancellationToken).ConfigureAwait(false);
        EnsureGitSuccess(init, "The materialized repository could not be initialized.");

        var add = await git.RunAsync(destination,
            AgentHomeGit.Arguments("add", "-A", "--", "."),
            cancellationToken).ConfigureAwait(false);
        EnsureGitSuccess(add, "The materialized repository contents could not be staged.");

        // Identity is supplied per-command rather than written into the new repository's config: the operator's own
        // user.name/user.email may be unset on this host, and a commit that fails for that reason would leave a
        // repository with no HEAD — which fails the workspace invariants later, far from the cause.
        var commit = await git.RunAsync(destination,
            AgentHomeGit.Arguments("-c", $"user.name={CommitAuthorName}",
                "-c", $"user.email={CommitAuthorEmail}",
                "commit", "--no-gpg-sign",
                "-m", $"Initial commit from template {templateAlias} @ {templateCommit}"),
            cancellationToken).ConfigureAwait(false);
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

        // S2.3: a project created by the user is a user artifact. Node data is where the engine's own managed
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
            cancellationToken).ConfigureAwait(false);
        EnsureGitSuccess(head, "The template has no resolvable HEAD commit.");
        return head.StandardOutput.Trim();
    }

    private async Task<string> ProbeAvailabilityAsync(string hostPath, CancellationToken cancellationToken)
    {
        try
        {
            var canonical = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(hostPath);
            await EnsureGitTopLevelAsync(canonical, cancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);
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

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public sealed class DevelopmentTemplateMaterializationException : InvalidOperationException
{
    public DevelopmentTemplateMaterializationException(string message) : base(message) { }
}
