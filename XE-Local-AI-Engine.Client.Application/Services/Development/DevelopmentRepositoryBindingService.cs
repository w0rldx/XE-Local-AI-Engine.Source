namespace XE_Local_AI_Engine.Client.Services.Development;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;

public sealed record DevelopmentRepositoryBinding(
    Guid ProjectId,
    Guid SelectedFolderId,
    string Alias,
    string RepositoryRoot,
    string RepositoryIdentityHash);

public sealed record DevelopmentRepositoryReference(string Id, string Alias, string Availability);

public interface IDevelopmentRepositoryBindingService
{
    Task<DevelopmentRepositoryReference> RegisterAsync(string displayAlias, string hostPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DevelopmentRepositoryReference>> ListAsync(CancellationToken cancellationToken = default);
    Task<DevelopmentRepositoryBinding> ResolveFolderAsync(Guid selectedFolderId, CancellationToken cancellationToken = default);
    Task<DevelopmentRepositoryBinding> ResolveProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<DevelopmentRepositoryBinding> ResolveExecutionAsync(DevelopmentExecutionSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<DevelopmentProjectSnapshot> ReconnectAsync(Guid projectId,
        Guid selectedFolderId,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}

internal sealed class DevelopmentRepositoryBindingService(
    ISelectedFolderResolver selectedFolders,
    IDevelopmentStore store,
    IOptions<DevelopmentOptions> options) : IDevelopmentRepositoryBindingService
{
    private readonly DevelopmentOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly ISelectedFolderResolver _selectedFolders = selectedFolders ?? throw new ArgumentNullException(nameof(selectedFolders));
    private readonly IDevelopmentStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<DevelopmentRepositoryReference> RegisterAsync(string displayAlias,
        string hostPath,
        CancellationToken cancellationToken = default)
    {
        var canonicalRoot = ResolveCanonicalRoot(hostPath);
        await EnsureLocalGitTopLevelAsync(canonicalRoot, cancellationToken).ConfigureAwait(false);
        var reference = await _selectedFolders.RegisterAsync(new SelectedFolderRegistration(displayAlias, canonicalRoot, SelectedFolderMode.Copy), cancellationToken)
                                              .ConfigureAwait(false);
        return new DevelopmentRepositoryReference(reference.Id, reference.Alias, "Available");
    }

    public async Task<IReadOnlyList<DevelopmentRepositoryReference>> ListAsync(CancellationToken cancellationToken = default)
    {
        var references = await _selectedFolders.ListReferencesAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<DevelopmentRepositoryReference>(references.Count);
        foreach (var reference in references)
        {
            var availability = "Available";
            try
            {
                _ = await ResolveFolderAsync(Guid.Parse(reference.Id), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is DevelopmentWorkspaceSecurityException
                                                  or SelectedFolderValidationException
                                                  or DirectoryNotFoundException)
            {
                availability = "Unavailable";
            }

            result.Add(new DevelopmentRepositoryReference(reference.Id, reference.Alias, availability));
        }

        return result;
    }

    public async Task<DevelopmentRepositoryBinding> ResolveFolderAsync(Guid selectedFolderId,
        CancellationToken cancellationToken = default)
    {
        if (selectedFolderId == Guid.Empty)
        {
            throw new DevelopmentWorkspaceSecurityException("A registered Development repository is required.");
        }

        var selected = await _selectedFolders.ResolveAsync(selectedFolderId.ToString(), cancellationToken).ConfigureAwait(false);
        if (selected.Mode != SelectedFolderMode.Copy)
        {
            throw new DevelopmentWorkspaceSecurityException("The selected folder is read-only and cannot be used for Development execution.");
        }

        var canonicalRoot = ResolveCanonicalRoot(selected.HostPath);
        await EnsureLocalGitTopLevelAsync(canonicalRoot, cancellationToken).ConfigureAwait(false);
        return new DevelopmentRepositoryBinding(Guid.Empty,
            selected.Id,
            selected.Alias,
            canonicalRoot,
            DevelopmentWorkspaceSecurity.RepositoryIdentityHash(canonicalRoot));
    }

    public async Task<DevelopmentRepositoryBinding> ResolveProjectAsync(Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await _store.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        return await ResolveProjectAsync(project, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentRepositoryBinding> ResolveExecutionAsync(DevelopmentExecutionSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SelectedFolderId is not { } selectedFolderId)
        {
            throw new DevelopmentWorkspaceSecurityException("The Development project repository must be reconnected before execution.");
        }

        var binding = await ResolveFolderAsync(selectedFolderId, cancellationToken).ConfigureAwait(false);
        EnsureIdentity(snapshot.RepositoryIdentityHash, binding.RepositoryIdentityHash);
        return binding with
        {
            ProjectId = snapshot.ProjectId
        };
    }

    public async Task<DevelopmentProjectSnapshot> ReconnectAsync(Guid projectId,
        Guid selectedFolderId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var project = await _store.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var binding = await ResolveFolderAsync(selectedFolderId, cancellationToken).ConfigureAwait(false);
        EnsureIdentity(project.RepositoryIdentityHash, binding.RepositoryIdentityHash);
        return await _store.ReconnectProjectRepositoryAsync(projectId, selectedFolderId, expectedVersion, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DevelopmentRepositoryBinding> ResolveProjectAsync(DevelopmentProjectSnapshot project,
        CancellationToken cancellationToken)
    {
        if (project.SelectedFolderId is not { } selectedFolderId)
        {
            throw new DevelopmentWorkspaceSecurityException("The Development project repository must be reconnected before this action.");
        }

        var binding = await ResolveFolderAsync(selectedFolderId, cancellationToken).ConfigureAwait(false);
        EnsureIdentity(project.RepositoryIdentityHash, binding.RepositoryIdentityHash);
        return binding with
        {
            ProjectId = project.Id
        };
    }

    private async Task EnsureLocalGitTopLevelAsync(string canonicalRoot, CancellationToken cancellationToken)
    {
        if (canonicalRoot.StartsWith("//", StringComparison.Ordinal)
            || canonicalRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new DevelopmentWorkspaceSecurityException("Network repository paths are not supported for Development execution.");
        }

        var git = new HostGitRunner(_options.MaxAttemptDurationSeconds);
        var result = await git.RunAsync(canonicalRoot,
            AgentHomeGit.Arguments("rev-parse", "--show-toplevel"),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new DevelopmentWorkspaceSecurityException("The selected folder must be a Git repository root.");
        }

        var topLevel = ResolveCanonicalRoot(result.StandardOutput.Trim());
        if (!PathEquals(canonicalRoot, topLevel))
        {
            throw new DevelopmentWorkspaceSecurityException("The selected folder must be the canonical Git repository root.");
        }
    }

    private static void EnsureIdentity(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new DevelopmentWorkspaceSecurityException("The selected repository no longer matches the Development project identity.");
        }
    }

    private static string ResolveCanonicalRoot(string hostPath)
    {
        try
        {
            return DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(hostPath);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            throw new DevelopmentWorkspaceSecurityException("The selected repository is unavailable.");
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
