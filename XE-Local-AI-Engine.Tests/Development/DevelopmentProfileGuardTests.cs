namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;
using PersistenceDevelopmentTaskStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentTaskStatus;

/// <summary>
///     The two guards that stop an attempt from rewriting the terms it is judged by: the <c>.xe-dev/profile.json</c>
///     tamper check, and the test-write policy.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class DevelopmentProfileGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-profile-guard-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>
    ///     The deny-list entry covers tool path arguments; this covers the case it cannot see. A catalog command can
    ///     write the import file as a build side effect, entirely outside the path guard, so the invariant re-check
    ///     after every catalog command is what actually carries the property.
    /// </summary>
    [Test]
    public async Task WorkspaceInvariant_WhenTheProfileImportFileIsRewritten_RejectsTheNextCatalogCommand()
    {
        var repository = await CreateRepositoryAsync();
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        var options = Options.Create(new DevelopmentOptions());
        var canonical = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository);
        var identity = DevelopmentWorkspaceSecurity.RepositoryIdentityHash(canonical);
        var snapshot = Snapshot(identity);

        using var sandbox = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), TimeProvider.System);
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository, identity));

        // The profile carries a DIFFERENT ImportDigest from what the worktree contains, to pin that the tamper check
        // baselines off the worktree rather than off this stored value. Comparing against the stored value would fail
        // this attempt on its first command purely because the repository has an uncommitted edit to the import file —
        // a false positive that would make Dev Mode unusable on any repository mid-edit.
        var profile = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit,
            buildTarget: null,
            templateId: null,
            importDigest: new string('b', 64));

        var tools = new DevelopmentWorkspaceTools(sandbox, session, options, profile);
        _ = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus);

        // Simulate a build or test command writing the file as a side effect. The path guard never sees this — nothing
        // named ".xe-dev" was passed to a workspace tool.
        var importDirectory = Path.Combine(session.HostWorktreePath, ".xe-dev");
        Directory.CreateDirectory(importDirectory);
        await File.WriteAllTextAsync(Path.Combine(importDirectory, "profile.json"),
                      """{"profileId":"generic-git","buildTarget":null}""");

        var rejection = await AssertEx
                              .ThrowsAsync<DevelopmentWorkspaceSecurityException>(() =>
                                  tools.RunCommandAsync(DevelopmentCommandIds.GitStatus));
        AssertEx.Contains(rejection.Message, "command-profile import file", StringComparison.Ordinal);
    }

    /// <summary>
    ///     The path guard covers the other half: the agent cannot name the import file as a tool argument either.
    /// </summary>
    [Test]
    public void PathGuard_RejectsTheProfileImportDirectoryAsAToolArgument()
    {
        AssertEx.False(DevelopmentWorkspaceSecurity.Confine(".xe-dev/profile.json", allowRoot: false).IsAccepted,
            "the command-profile import file must not be writable through a workspace tool path");
        AssertEx.False(DevelopmentWorkspaceSecurity.Confine(".xe-dev", allowRoot: true).IsAccepted,
            "the command-profile import directory must not be reachable through a workspace tool path");
    }

    /// <summary>
    ///     The test-write policy, stated as the behaviour that matters: adding tests is allowed, removing or weakening
    ///     an existing one is
    ///     not. The "added" case is not a formality — the change types are mapped words rather than git's status
    ///     letters, and comparing against the letters silently rejects every new test instead.
    /// </summary>
    [Test]
    public async Task TestWritePolicy_AllowsAddedAndCopiedTestsAndRejectsEveryDestructiveChangeToAnExistingOne()
    {
        var profile = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

        DevelopmentTestWritePolicy.Ensure(Evidence(new DevelopmentChangedFile { Path = "src/Lib/Feature.cs", ChangeType = "modified" },
                new DevelopmentChangedFile { Path = "tests/Probe/NewFeatureTests.cs", ChangeType = "added" },
                new DevelopmentChangedFile { Path = "tests/Probe/CopiedTests.cs", ChangeType = "copied", PreviousPath = "tests/Probe/FeatureTests.cs" }),
            profile);

        foreach (var destructive in new[]
                 {
                     "modified",
                     "deleted",
                     "typechanged",
                     "unknown"
                 })
        {
            var rejected = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() =>
            {
                DevelopmentTestWritePolicy.Ensure(Evidence(new DevelopmentChangedFile { Path = "tests/Probe/FeatureTests.cs", ChangeType = destructive }), profile);
                return Task.CompletedTask;
            });
            AssertEx.Contains(rejected.Message, "test that existed at the base commit", StringComparison.Ordinal);
        }

        // Renaming a protected test out of the protected set removes coverage exactly as a delete would, so the
        // PREVIOUS path has to be checked even though the new one looks innocuous.
        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() =>
        {
            DevelopmentTestWritePolicy.Ensure(Evidence(new DevelopmentChangedFile { Path = "tests/Probe/Feature.txt", ChangeType = "renamed", PreviousPath = "tests/Probe/FeatureTests.cs" }),
                profile);
            return Task.CompletedTask;
        });

        // A non-test file may be freely modified or deleted; the policy is about tests, not about change in general.
        DevelopmentTestWritePolicy.Ensure(Evidence(new DevelopmentChangedFile { Path = "src/Lib/Feature.cs", ChangeType = "deleted" }), profile);
    }

    private static DevelopmentPatchEvidence Evidence(params DevelopmentChangedFile[] changedFiles) =>
        new()
        {
            BaseCommit = "0000000000000000000000000000000000000000",
            PatchHash = "patch",
            ManifestHash = "manifest",
            SubjectHash = "subject",
            ExpectedResultHash = "expected",
            PatchBytes = [1],
            ManifestBytes = [1],
            ChangedFiles = changedFiles
        };

    private static DevelopmentRepositoryBinding Binding(DevelopmentExecutionSnapshot snapshot, string repositoryRoot, string identity) =>
        new() { ProjectId = snapshot.ProjectId, SelectedFolderId = snapshot.SelectedFolderId ?? Guid.NewGuid(), Alias = "fixture", RepositoryRoot = repositoryRoot, RepositoryIdentityHash = identity };

    private static DevelopmentExecutionSnapshot Snapshot(string identityHash) =>
        new()
        {
            ProjectId = Guid.NewGuid(),
            TaskId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            SelectedFolderId = Guid.NewGuid(),
            RepositoryIdentityHash = identityHash,
            BaseBranch = "main",
            EgressPolicy = DevelopmentEgressPolicy.LocalOnly,
            ConfigurationVersion = 1,
            TrustedRepositoryAcknowledged = true,
            TrustedRepositoryPolicyVersion = DevelopmentTrustPolicy.CurrentVersion,
            TrustedRepositoryAcknowledgedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MaxTokens = null,
            MaxDurationSeconds = null,
            Title = "title",
            Requirements = "requirements",
            AcceptanceCriteriaJson = "[]",
            TaskStatus = PersistenceDevelopmentTaskStatus.InProgress,
            TaskVersion = 1,
            AttemptRole = DevelopmentAttemptRole.Coder,
            AttemptStatus = PersistenceDevelopmentAttemptStatus.Running,
            ModelId = "model",
            Provider = "local",
            AttemptVersion = 1,
            // A real execution snapshot always carries the project's stored profile, and PrepareAsync now reads it to
            // decide whether the base commit needs a dependency warm restore. The generic profile declares no restore
            // command, so this fixture warms nothing — which is what keeps this test about the import tamper check.
            // The tools below deliberately bind a DIFFERENT profile object; see the comment at that call site.
            CommandProfileJson = Encoding.UTF8.GetString(DevelopmentCommandProfileCatalog
                                                        .Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null)
                                                        .ToCanonicalUtf8())
        };

    private async Task<string> CreateRepositoryAsync()
    {
        var repository = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "guard fixture\n");
        await RunGitAsync(repository, "init", "--initial-branch=main");
        await RunGitAsync(repository, "config", "user.email", "development-guard@example.invalid");
        await RunGitAsync(repository, "config", "user.name", "Development Guard Test");
        await RunGitAsync(repository, "add", "-A", "--", ".");
        await RunGitAsync(repository, "commit", "-m", "guard fixture");
        return repository;
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("git could not be started.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {await process.StandardError.ReadToEndAsync()}");
        }
    }
}
