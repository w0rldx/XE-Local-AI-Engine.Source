namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

[Category(TestCategories.Integration)]
public sealed class DevelopmentRepositoryBindingServiceTests : IDisposable
{
    /// <summary>
    ///     The snapshotted profile these fixtures carry. Binding resolution never runs a command, so the generic
    ///     profile — the one code-owned profile that needs no build target — is the honest value here.
    /// </summary>
    private static readonly string GenericProfileJson =
        Encoding.UTF8.GetString(DevelopmentCommandProfileCatalog
                                .Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null)
                                .ToCanonicalUtf8());

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-repository-binding-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
    }

    [Test]
    public async Task RegisterAsync_WhenPathIsCanonicalGitRoot_RegistersCopyFolderAndReturnsAvailableReference()
    {
        var repository = await CreateRepositoryAsync();
        var selectedFolderId = Guid.NewGuid();
        var selectedFolders = Substitute.For<ISelectedFolderResolver>();
        selectedFolders.RegisterAsync(Arg.Any<SelectedFolderRegistration>(), Arg.Any<CancellationToken>())
                       .Returns(new SelectedFolderReference { Id = selectedFolderId.ToString(), Alias = "repo" });
        var service = CreateService(selectedFolders);

        var result = await service.RegisterAsync("Repo", repository);

        AssertEx.Equal(selectedFolderId.ToString(), result.Id);
        AssertEx.Equal("repo", result.Alias);
        AssertEx.Equal("Available", result.Availability);
        _ = selectedFolders.Received(1).RegisterAsync(Arg.Is<SelectedFolderRegistration>(registration => registration.Alias == "Repo"
                                                                                                         && registration.HostPath == DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)
                                                                                                         && registration.Mode == SelectedFolderMode.Copy),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveFolderAsync_WhenRegisteredFolderIsCanonicalGitRoot_ReturnsBoundRepositoryIdentity()
    {
        var repository = await CreateRepositoryAsync();
        var selectedFolderId = Guid.NewGuid();
        var selectedFolders = Substitute.For<ISelectedFolderResolver>();
        selectedFolders.ResolveAsync(selectedFolderId.ToString(), Arg.Any<CancellationToken>())
                       .Returns(new ResolvedSelectedFolder { Id = selectedFolderId, Alias = "repo", HostPath = repository, Mode = SelectedFolderMode.Copy });
        var service = CreateService(selectedFolders);

        var result = await service.ResolveFolderAsync(selectedFolderId);

        var canonicalRoot = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository);
        AssertEx.Equal(Guid.Empty, result.ProjectId);
        AssertEx.Equal(selectedFolderId, result.SelectedFolderId);
        AssertEx.Equal("repo", result.Alias);
        AssertEx.Equal(canonicalRoot, result.RepositoryRoot);
        AssertEx.Equal(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(canonicalRoot), result.RepositoryIdentityHash);
    }

    [Test]
    public async Task RegisterAsync_WhenFolderIsNotGitRepository_ThrowsSecurityException()
    {
        var directory = Path.Combine(_root, "not-git");
        Directory.CreateDirectory(directory);
        var service = CreateService(Substitute.For<ISelectedFolderResolver>());

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => service.RegisterAsync("not-git", directory));
    }

    [Test]
    public async Task RegisterAsync_WhenFolderDoesNotExist_ThrowsSanitizedSecurityException()
    {
        var missingDirectory = Path.Combine(_root, "missing");
        var service = CreateService(Substitute.For<ISelectedFolderResolver>());

        var exception = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => service.RegisterAsync("missing", missingDirectory));

        AssertEx.Equal("The selected repository is unavailable.", exception.Message);
        AssertEx.False(exception.ToString().Contains(missingDirectory, StringComparison.Ordinal));
    }

    [Test]
    public async Task RegisterAsync_WhenFolderIsBelowGitRoot_ThrowsSecurityException()
    {
        var repository = await CreateRepositoryAsync();
        var nestedFolder = Path.Combine(repository, "src");
        Directory.CreateDirectory(nestedFolder);
        var service = CreateService(Substitute.For<ISelectedFolderResolver>());

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => service.RegisterAsync("nested", nestedFolder));
    }

    [Test]
    public async Task ResolveFolderAsync_WhenRegisteredPathTraversesSymlink_ThrowsSecurityException()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("Creating symbolic links is privilege-dependent on Windows.");
            return;
        }

        var repository = await CreateRepositoryAsync();
        var linkedRepository = Path.Combine(_root, "repository-link");
        _ = Directory.CreateSymbolicLink(linkedRepository, repository);
        var selectedFolderId = Guid.NewGuid();
        var selectedFolders = Substitute.For<ISelectedFolderResolver>();
        selectedFolders.ResolveAsync(selectedFolderId.ToString(), Arg.Any<CancellationToken>())
                       .Returns(new ResolvedSelectedFolder { Id = selectedFolderId, Alias = "repo", HostPath = linkedRepository, Mode = SelectedFolderMode.Copy });
        var service = CreateService(selectedFolders);

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => service.ResolveFolderAsync(selectedFolderId));
    }

    /// <summary>
    ///     The junction twin of <see cref="ResolveFolderAsync_WhenRegisteredPathTraversesSymlink_ThrowsSecurityException" />,
    ///     which skips outright on Windows. Junctions are the same reparse point and need no privilege, so the
    ///     registered-path resolution guard is proven on the shipping platform rather than assumed from Linux.
    /// </summary>
    [Test]
    public async Task ResolveFolderAsync_WhenRegisteredPathTraversesJunction_ThrowsSecurityException()
    {
        JunctionSupport.EnsureSupported();

        var repository = await CreateRepositoryAsync();
        var linkedRepository = Path.Combine(_root, "repository-junction");
        AssertEx.True(JunctionSupport.TryCreate(linkedRepository, repository),
            "the fixture must be able to plant a junction once EnsureSupported has passed");
        var selectedFolderId = Guid.NewGuid();
        var selectedFolders = Substitute.For<ISelectedFolderResolver>();
        selectedFolders.ResolveAsync(selectedFolderId.ToString(), Arg.Any<CancellationToken>())
                       .Returns(new ResolvedSelectedFolder { Id = selectedFolderId, Alias = "repo", HostPath = linkedRepository, Mode = SelectedFolderMode.Copy });
        var service = CreateService(selectedFolders);

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => service.ResolveFolderAsync(selectedFolderId));
    }

    [Test]
    public async Task ResolveFolderAsync_WhenRegisteredFolderIsReadOnly_ThrowsSecurityException()
    {
        var repository = await CreateRepositoryAsync();
        var selectedFolderId = Guid.NewGuid();
        var selectedFolders = Substitute.For<ISelectedFolderResolver>();
        selectedFolders.ResolveAsync(selectedFolderId.ToString(), Arg.Any<CancellationToken>())
                       .Returns(new ResolvedSelectedFolder { Id = selectedFolderId, Alias = "repo", HostPath = repository, Mode = SelectedFolderMode.ReadOnlyMount });
        var service = CreateService(selectedFolders);

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => service.ResolveFolderAsync(selectedFolderId));
    }

    [Test]
    public async Task ResolveExecutionAsync_WhenRepositoryIdentityDoesNotMatch_ThrowsSecurityException()
    {
        var repository = await CreateRepositoryAsync();
        var selectedFolderId = Guid.NewGuid();
        var selectedFolders = Substitute.For<ISelectedFolderResolver>();
        selectedFolders.ResolveAsync(selectedFolderId.ToString(), Arg.Any<CancellationToken>())
                       .Returns(new ResolvedSelectedFolder { Id = selectedFolderId, Alias = "repo", HostPath = repository, Mode = SelectedFolderMode.Copy });
        var service = CreateService(selectedFolders);

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => service.ResolveExecutionAsync(ExecutionSnapshot(selectedFolderId, repositoryIdentityHash: "different-repository")));
    }

    [Test]
    public async Task ReconnectAsync_WhenRepositoryIdentityMatches_DelegatesToStore()
    {
        var repository = await CreateRepositoryAsync();
        var canonicalRoot = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository);
        var repositoryIdentityHash = DevelopmentWorkspaceSecurity.RepositoryIdentityHash(canonicalRoot);
        var projectId = Guid.NewGuid();
        var selectedFolderId = Guid.NewGuid();
        const long expectedVersion = 7;
        var selectedFolders = Substitute.For<ISelectedFolderResolver>();
        selectedFolders.ResolveAsync(selectedFolderId.ToString(), Arg.Any<CancellationToken>())
                       .Returns(new ResolvedSelectedFolder { Id = selectedFolderId, Alias = "repo", HostPath = repository, Mode = SelectedFolderMode.Copy });
        var store = Substitute.For<IDevelopmentStore>();
        var disconnectedProject = ProjectSnapshot(projectId, selectedFolderId: null, repositoryIdentityHash, version: expectedVersion);
        var reconnectedProject = disconnectedProject with
        {
            SelectedFolderId = selectedFolderId,
            Version = expectedVersion + 1
        };
        store.GetProjectAsync(projectId, Arg.Any<CancellationToken>()).Returns(disconnectedProject);
        store.ReconnectProjectRepositoryAsync(projectId, selectedFolderId, expectedVersion, Arg.Any<CancellationToken>())
             .Returns(reconnectedProject);
        var service = CreateService(selectedFolders, store);

        var result = await service.ReconnectAsync(projectId, selectedFolderId, expectedVersion);

        AssertEx.Equal(reconnectedProject, result);
        _ = store.Received(1).ReconnectProjectRepositoryAsync(projectId,
            selectedFolderId,
            expectedVersion,
            Arg.Any<CancellationToken>());
    }

    private static DevelopmentRepositoryBindingService CreateService(ISelectedFolderResolver selectedFolders,
        IDevelopmentStore? store = null) =>
        new(selectedFolders,
            store ?? Substitute.For<IDevelopmentStore>(),
            Options.Create(new DevelopmentOptions
            {
                MaxAttemptDurationSeconds = 30
            }));

    private async Task<string> CreateRepositoryAsync()
    {
        var repository = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        var result = await RunProcessAsync(repository, "git", "init", "--initial-branch=main", ".");
        AssertEx.Equal(expected: 0, result.ExitCode, result.StandardError);
        return repository;
    }

    private static async Task<CommandResult> RunProcessAsync(string workingDirectory,
        string executable,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CommandResult { ExitCode = process.ExitCode, StandardOutput = await stdout, StandardError = await stderr };
    }

    private static DevelopmentExecutionSnapshot ExecutionSnapshot(Guid selectedFolderId, string repositoryIdentityHash) =>
        new()
        {
            ProjectId = Guid.NewGuid(),
            TaskId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            SelectedFolderId = selectedFolderId,
            RepositoryIdentityHash = repositoryIdentityHash,
            BaseBranch = "main",
            EgressPolicy = DevelopmentEgressPolicy.LocalOnly,
            ConfigurationVersion = 1,
            TrustedRepositoryAcknowledged = true,
            TrustedRepositoryPolicyVersion = DevelopmentTrustPolicy.CurrentVersion,
            TrustedRepositoryAcknowledgedAtUtc = 1,
            MaxTokens = null,
            MaxDurationSeconds = null,
            Title = "task",
            Requirements = "requirements",
            AcceptanceCriteriaJson = "[]",
            TaskStatus = DevelopmentTaskStatus.InProgress,
            TaskVersion = 1,
            AttemptRole = DevelopmentAttemptRole.Coder,
            AttemptStatus = PersistenceDevelopmentAttemptStatus.Running,
            ModelId = "model",
            Provider = "local",
            AttemptVersion = 1,
            CommandProfileJson = GenericProfileJson
        };

    private static DevelopmentProjectSnapshot ProjectSnapshot(Guid projectId,
        Guid? selectedFolderId,
        string repositoryIdentityHash,
        long version) =>
        new()
        {
            Id = projectId,
            Objective = "objective",
            SelectedFolderId = selectedFolderId,
            RepositoryIdentityHash = repositoryIdentityHash,
            BaseBranch = "main",
            Status = DevelopmentProjectStatus.Active,
            EgressPolicy = DevelopmentEgressPolicy.LocalOnly,
            CoderModelId = "coder-model",
            ReviewerModelId = "reviewer-model",
            MaxTokens = null,
            MaxDurationSeconds = null,
            ConfigurationVersion = 1,
            TrustedRepositoryAcknowledged = true,
            TrustedRepositoryPolicyVersion = DevelopmentTrustPolicy.CurrentVersion,
            TrustedRepositoryAcknowledgedAtUtc = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1,
            Version = version,
            CommandProfileJson = GenericProfileJson
        };

    private sealed record CommandResult
    {
        public required int ExitCode { get; init; }

        public required string StandardOutput { get; init; }

        public required string StandardError { get; init; }
    }
}
