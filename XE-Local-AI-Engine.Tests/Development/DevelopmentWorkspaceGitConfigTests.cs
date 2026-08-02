namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using Microsoft.Extensions.Options;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The engine-side, provider-independent closure of the repository-local <c>.git/config</c> execution vector.
///     <para>
///         Two payloads, both measured on git 2.53.0 before this existed. <c>core.fsmonitor</c> runs as a shell command
///         on index refresh — <c>AgentHomeGit</c>'s <c>-c</c> pins already close that one. <c>filter.&lt;driver&gt;.clean</c>
///         selected by an in-tree <c>.gitattributes</c> runs on <c>git add</c>, and it CANNOT be closed from the
///         argument vector: driver names are arbitrary so there is no finite set to pin, and
///         <c>core.attributesfile=/dev/null</c> disables only the GLOBAL attributes file, which an in-tree
///         <c>.gitattributes</c> outranks. Removing the definition is what closes it, because a driver that is not
///         defined cannot run whatever the attributes file selects.
///     </para>
///     <para>
///         This is host-side execution, not sandbox-side: <see cref="DevelopmentPatchEvidenceService" /> runs
///         <c>reset</c> and <c>add -A</c> with the workspace as its working directory, on the machine running the
///         engine. And the standalone clone is what OPENED it — before it, the workspace's <c>.git</c> was a pointer
///         file and repository-local config lived outside the jail entirely.
///     </para>
/// </summary>
public sealed class DevelopmentWorkspaceGitConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-gitconfig-" + Guid.NewGuid().ToString("N"));

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
    public void RestoreMinimal_KeepsTheKeysGitRefusesToOperateWithout()
    {
        // Minimal is NOT empty. A clone of a repository using a newer object format carries extensions.* keys that git
        // refuses to open the repository without, and core.repositoryformatversion is what selects that rule — so
        // truncating the file would turn a security fix into an outage on exactly the repositories most likely to
        // matter.
        var workspace = CreateWorkspace("""
                                        [core]
                                        	repositoryformatversion = 1
                                        	filemode = true
                                        	bare = false
                                        	logallrefupdates = true
                                        [extensions]
                                        	objectformat = sha256
                                        	refstorage = reftable
                                        """);

        DevelopmentWorkspaceGitConfig.RestoreMinimal(workspace);
        var config = ReadConfig(workspace);

        AssertEx.Contains(config, "repositoryformatversion = 1");
        AssertEx.Contains(config, "filemode = true");
        AssertEx.Contains(config, "bare = false");
        AssertEx.Contains(config, "objectformat = sha256");
        AssertEx.Contains(config, "refstorage = reftable");
    }

    [Test]
    public void RestoreMinimal_RemovesEveryExecBearingDefinitionIncludingOnesNobodyEnumerated()
    {
        var workspace = CreateWorkspace("""
                                        [core]
                                        	repositoryformatversion = 0
                                        	fsmonitor = /tmp/pwn.sh
                                        	sshCommand = /tmp/pwn.sh
                                        	pager = /tmp/pwn.sh
                                        	editor = /tmp/pwn.sh
                                        	hooksPath = /tmp/hooks
                                        [filter "anything-at-all"]
                                        	clean = /tmp/pwn.sh
                                        	smudge = /tmp/pwn.sh
                                        [diff "custom"]
                                        	textconv = /tmp/pwn.sh
                                        [include]
                                        	path = /tmp/evil-config
                                        """);

        DevelopmentWorkspaceGitConfig.RestoreMinimal(workspace);
        var config = ReadConfig(workspace);

        // Asserted as the absence of the PAYLOAD rather than of specific key names: the property is that no definition
        // survives, whatever it was called.
        AssertEx.False(config.Contains("pwn.sh", StringComparison.Ordinal), config);
        AssertEx.False(config.Contains("filter", StringComparison.OrdinalIgnoreCase), config);
        AssertEx.False(config.Contains("include", StringComparison.OrdinalIgnoreCase), config);
        AssertEx.Contains(config, "repositoryformatversion = 0");
    }

    [Test]
    public void RestoreMinimal_DoesNotReintroduceARemote()
    {
        // The clone drops origin deliberately — it points straight back at the trusted source repository — and a test
        // asserts the workspace has no remote. Restoring it here would quietly undo D8.
        var workspace = CreateWorkspace("""
                                        [core]
                                        	repositoryformatversion = 0
                                        [remote "origin"]
                                        	url = /home/user/projects/trusted-source
                                        	fetch = +refs/heads/*:refs/remotes/origin/*
                                        """);

        DevelopmentWorkspaceGitConfig.RestoreMinimal(workspace);

        AssertEx.False(ReadConfig(workspace).Contains("origin", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void RestoreMinimal_ClosesTheSecondConfigFileRatherThanLeavingItUnsanitised()
    {
        // extensions.worktreeConfig makes git read .git/config.worktree, which this rewrite does not cover — so keeping
        // the extension would leave an unsanitised second file holding whatever the first one may no longer hold. A
        // standalone clone has no linked worktrees, so the extension is dropped and the file removed.
        var workspace = CreateWorkspace("""
                                        [core]
                                        	repositoryformatversion = 0
                                        [extensions]
                                        	worktreeConfig = true
                                        """);
        var worktreeConfig = Path.Combine(workspace, ".git", "config.worktree");
        File.WriteAllText(worktreeConfig, "[core]\n\tfsmonitor = /tmp/pwn.sh\n");

        DevelopmentWorkspaceGitConfig.RestoreMinimal(workspace);

        AssertEx.False(File.Exists(worktreeConfig));
        AssertEx.False(ReadConfig(workspace).Contains("worktreeConfig", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void RestoreMinimal_WhenTheConfigWasSwappedForASymlink_WritesTheRealFileRatherThanThroughTheLink()
    {
        // A command running in the workspace can replace the file with a link; an ordinary write would then follow it
        // out of the workspace and rewrite whatever it points at.
        var workspace = CreateWorkspace("[core]\n\trepositoryformatversion = 0\n");
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "untouched");
        var configPath = Path.Combine(workspace, ".git", "config");
        File.Delete(configPath);
        File.CreateSymbolicLink(configPath, outside);

        DevelopmentWorkspaceGitConfig.RestoreMinimal(workspace);

        AssertEx.Equal("untouched", File.ReadAllText(outside));
        AssertEx.Null(File.ResolveLinkTarget(configPath, returnFinalTarget: false));
    }

    [Test]
    public async Task PatchEvidenceExport_WithBothPayloadsPlanted_ExecutesNeitherOfThem()
    {
        // The end-to-end regression pin, through the real service that runs host-side git. Both payloads are planted in
        // the workspace's own .git/config exactly as a build or test command could write them — DevelopmentWorkspaceSecurity
        // blocks the workspace TOOLS from naming that path, but nothing stops a command writing it as a side effect.
        var fixture = await CreatePoisonedWorkspaceAsync().ConfigureAwait(false);
        var service = new DevelopmentPatchEvidenceService(Options.Create(OptionsValue()));

        var evidence = await service.ExportAsync(fixture.Session).ConfigureAwait(false);

        AssertEx.False(File.Exists(fixture.FilterSentinel), "the filter.<driver>.clean payload executed on the host.");
        AssertEx.False(File.Exists(fixture.FsmonitorSentinel), "the core.fsmonitor payload executed on the host.");
        // And the export still WORKS — a fix that broke evidence export would pass the two assertions above for the
        // wrong reason.
        AssertEx.NotNullOrEmpty(evidence.SubjectHash);
        AssertEx.Contains(evidence.ChangedFiles.Select(static file => file.Path), "feature.txt");
    }

    [Test]
    public async Task PatchEvidenceExport_WithoutTheRewrite_TheFilterPayloadDoesExecute()
    {
        // The control that makes the test above mean something. It runs the same planted repository through the same
        // hardened argument vector the service uses, WITHOUT the rewrite, and asserts the payload fires — so a green
        // result above is evidence the rewrite worked rather than evidence the payload was never reachable.
        var fixture = await CreatePoisonedWorkspaceAsync().ConfigureAwait(false);

        await DevelopmentMountBrokerTests.RunGitAsync(fixture.Session.HostWorktreePath,
            [.. HardenedArguments(), "add", "-A", "--", "."]).ConfigureAwait(false);

        AssertEx.True(File.Exists(fixture.FilterSentinel),
            "the filter.<driver>.clean payload did NOT execute under the hardened argument vector, so the regression pin above proves nothing.");
    }

    private static string[] HardenedArguments()
    {
        // The vector DevelopmentPatchEvidenceService actually runs under, so the control test is not weaker than the
        // production path it is standing in for.
        return [.. AgentHomeGit.Arguments()];
    }

    private async Task<PoisonedWorkspace> CreatePoisonedWorkspaceAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            // The payload is a shell script, and a Windows equivalent would exercise a different execution mechanism
            // rather than the same one. Skipped with a reason rather than silently passing on a platform where these
            // two tests would prove nothing.
            throw new SkipTestException("SKIPPED — the .git/config execution payloads are POSIX shell scripts. This pin runs on Linux and macOS.");
        }

        var workspace = Path.Combine(_root, "poisoned-" + Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(_root, "runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(runtime);

        await DevelopmentMountBrokerTests.RunGitAsync(workspace, "init", "--initial-branch=main", ".").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(workspace, "config", "user.email", "gitconfig@example.invalid").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(workspace, "config", "user.name", "Git Config Test").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "base\n").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(workspace, "add", "README.md").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(workspace, "commit", "-m", "base").ConfigureAwait(false);
        var baseCommit = await ReadHeadAsync(workspace).ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(workspace, "checkout", "--detach", baseCommit).ConfigureAwait(false);

        var filterSentinel = Path.Combine(_root, "SENTINEL-FILTER-" + Guid.NewGuid().ToString("N"));
        var fsmonitorSentinel = Path.Combine(_root, "SENTINEL-FSMONITOR-" + Guid.NewGuid().ToString("N"));

        // A script rather than an inline shell line: git's config parser mangles nested quotes, and a payload that
        // fails to parse would look like a payload that was blocked.
        var payload = Path.Combine(_root, "payload-" + Guid.NewGuid().ToString("N") + ".sh");
        await File.WriteAllTextAsync(payload,
            $"#!/bin/sh\ntouch \"{filterSentinel}\"\ntouch \"{fsmonitorSentinel}\"\ncat\n").ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(payload,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }

        await File.AppendAllTextAsync(Path.Combine(workspace, ".git", "config"),
            $"""

             [core]
             	fsmonitor = {payload}
             [filter "pwn"]
             	clean = {payload}
             	smudge = cat

             """).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(workspace, ".gitattributes"), "* filter=pwn\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(workspace, "feature.txt"), "implemented\n").ConfigureAwait(false);

        var session = new DevelopmentWorkspaceSession(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            baseCommit,
            "identity",
            workspace,
            runtime,
            new SandboxHandle
            {
                ProviderName = "process",
                SandboxId = "sandbox",
                AttachKey = new SandboxAttachKey
                {
                    OwnerUserId = "owner",
                    NodeId = "node",
                    ProviderName = "process",
                    RuntimeProfile = "development",
                    ManifestVersion = 2
                },
                CreatedAt = DateTimeOffset.UnixEpoch,
                ManifestVersion = 2
            });

        return new PoisonedWorkspace(session, filterSentinel, fsmonitorSentinel);
    }

    private static async Task<string> ReadHeadAsync(string workspace)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("HEAD");

        using var process = new Process
        {
            StartInfo = startInfo
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return output.Trim();
    }

    private static DevelopmentOptions OptionsValue() =>
        new()
        {
            Enabled = true,
            MaxArtifactBytes = 2 * 1024 * 1024,
            MaxPatchBytes = 1024 * 1024,
            MaxFileWriteBytes = 1024 * 1024,
            MaxCommandOutputBytes = 256 * 1024,
            MaxChangedFiles = 32,
            MaxToolCalls = 16,
            MaxAttemptDurationSeconds = 60,
            MaxOutputTokens = 2048
        };

    private string CreateWorkspace(string configContent)
    {
        var workspace = Path.Combine(_root, "workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        File.WriteAllText(Path.Combine(workspace, ".git", "config"), configContent);
        return workspace;
    }

    private static string ReadConfig(string workspace) =>
        File.ReadAllText(Path.Combine(workspace, ".git", "config"));

    private sealed record PoisonedWorkspace(DevelopmentWorkspaceSession Session, string FilterSentinel, string FsmonitorSentinel);
}
