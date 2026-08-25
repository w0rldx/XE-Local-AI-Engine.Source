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
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        var options = Options.Create(new DevelopmentOptions());
        var canonical = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository);
        var identity = DevelopmentWorkspaceSecurity.RepositoryIdentityHash(canonical);
        var snapshot = Snapshot(identity);

        using var sandbox = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), TimeProvider.System);
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System);
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository, identity)).ConfigureAwait(false);

        // The profile carries a DIFFERENT ImportDigest from what the worktree contains, to pin that the tamper check
        // baselines off the worktree rather than off this stored value. Comparing against the stored value would fail
        // this attempt on its first command purely because the repository has an uncommitted edit to the import file —
        // a false positive that would make Dev Mode unusable on any repository mid-edit.
        var profile = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit,
            buildTarget: null,
            templateId: null,
            importDigest: new string('b', 64));

        var tools = new DevelopmentWorkspaceTools(sandbox, session, options, profile);
        _ = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus).ConfigureAwait(false);

        // Simulate a build or test command writing the file as a side effect. The path guard never sees this — nothing
        // named ".xe-dev" was passed to a workspace tool.
        var importDirectory = Path.Combine(session.HostWorktreePath, ".xe-dev");
        Directory.CreateDirectory(importDirectory);
        await File.WriteAllTextAsync(Path.Combine(importDirectory, "profile.json"),
                      """{"profileId":"generic-git","buildTarget":null}""")
                  .ConfigureAwait(false);

        var rejection = await AssertEx
                              .ThrowsAsync<DevelopmentWorkspaceSecurityException>(() =>
                                  tools.RunCommandAsync(DevelopmentCommandIds.GitStatus))
                              .ConfigureAwait(false);
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

        DevelopmentTestWritePolicy.Ensure(Evidence(new DevelopmentChangedFile("src/Lib/Feature.cs", "modified"),
                new DevelopmentChangedFile("tests/Probe/NewFeatureTests.cs", "added"),
                new DevelopmentChangedFile("tests/Probe/CopiedTests.cs", "copied", "tests/Probe/FeatureTests.cs")),
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
                DevelopmentTestWritePolicy.Ensure(Evidence(new DevelopmentChangedFile("tests/Probe/FeatureTests.cs", destructive)), profile);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            AssertEx.Contains(rejected.Message, "test that existed at the base commit", StringComparison.Ordinal);
        }

        // Renaming a protected test out of the protected set removes coverage exactly as a delete would, so the
        // PREVIOUS path has to be checked even though the new one looks innocuous.
        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() =>
        {
            DevelopmentTestWritePolicy.Ensure(Evidence(new DevelopmentChangedFile("tests/Probe/Feature.txt", "renamed", "tests/Probe/FeatureTests.cs")),
                profile);
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        // A non-test file may be freely modified or deleted; the policy is about tests, not about change in general.
        DevelopmentTestWritePolicy.Ensure(Evidence(new DevelopmentChangedFile("src/Lib/Feature.cs", "deleted")), profile);
    }

    private static DevelopmentPatchEvidence Evidence(params DevelopmentChangedFile[] changedFiles) =>
        new("0000000000000000000000000000000000000000",
            PatchHash: "patch",
            ManifestHash: "manifest",
            SubjectHash: "subject",
            ExpectedResultHash: "expected",
            PatchBytes: [1],
            ManifestBytes: [1],
            changedFiles);

    private static DevelopmentRepositoryBinding Binding(DevelopmentExecutionSnapshot snapshot, string repositoryRoot, string identity) =>
        new(snapshot.ProjectId, snapshot.SelectedFolderId ?? Guid.NewGuid(), "fixture", repositoryRoot, identity);

    private static DevelopmentExecutionSnapshot Snapshot(string identityHash) =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            identityHash,
            "main",
            DevelopmentEgressPolicy.LocalOnly,
            ConfigurationVersion: 1,
            TrustedRepositoryAcknowledged: true,
            DevelopmentTrustPolicy.CurrentVersion,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MaxTokens: null,
            MaxDurationSeconds: null,
            "title",
            "requirements",
            "[]",
            PersistenceDevelopmentTaskStatus.InProgress,
            TaskVersion: 1,
            DevelopmentAttemptRole.Coder,
            PersistenceDevelopmentAttemptStatus.Running,
            "model",
            "local",
            AttemptVersion: 1,

            // A real execution snapshot always carries the project's stored profile, and PrepareAsync now reads it to
            // decide whether the base commit needs a dependency warm restore. The generic profile declares no restore
            // command, so this fixture warms nothing — which is what keeps this test about the import tamper check.
            // The tools below deliberately bind a DIFFERENT profile object; see the comment at that call site.
            CommandProfileJson: Encoding.UTF8.GetString(DevelopmentCommandProfileCatalog
                                                        .Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null)
                                                        .ToCanonicalUtf8()));

    private async Task<string> CreateRepositoryAsync()
    {
        var repository = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "guard fixture\n").ConfigureAwait(false);
        await RunGitAsync(repository, "init", "--initial-branch=main").ConfigureAwait(false);
        await RunGitAsync(repository, "config", "user.email", "development-guard@example.invalid").ConfigureAwait(false);
        await RunGitAsync(repository, "config", "user.name", "Development Guard Test").ConfigureAwait(false);
        await RunGitAsync(repository, "add", "-A", "--", ".").ConfigureAwait(false);
        await RunGitAsync(repository, "commit", "-m", "guard fixture").ConfigureAwait(false);
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
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {await process.StandardError.ReadToEndAsync().ConfigureAwait(false)}");
        }
    }
}
