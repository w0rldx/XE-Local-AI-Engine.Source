namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using NSubstitute;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

/// <summary>
///     G1(c): the agent-facing Development sandbox asks for <see cref="SandboxNetworkPolicy.None" />.
///     <para>
///         The request is capability-gated (the operator's 2026-08-25 Option B ruling), so BOTH directions are asserted
///         here. A test that only pinned the denial would pass on this Linux host and say nothing about the Windows
///         node where the process backend cannot confine networking and Development Mode must keep running — and the
///         fallback is the honest limitation of this design, not an accident to be left undocumented.
///     </para>
/// </summary>
public sealed class DevelopmentSandboxEgressTests : IDisposable
{
    private static readonly DevelopmentCommandProfile GenericProfile =
        DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-dev-egress-" + Guid.NewGuid().ToString("N")[..12]);

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
    public async Task PrepareAsync_OnABackendThatAdvertisesNetworkPolicy_RequestsNoEgressForTheAgentFacingSandbox()
    {
        var sandbox = new CapabilitySandbox(SandboxProviderCapabilities.SupportsTrustedHostWorkspace
                                            | SandboxProviderCapabilities.SupportsNetworkPolicy
                                            | SandboxProviderCapabilities.SupportsKill);

        _ = await PrepareAsync(sandbox).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, sandbox.Created.Count);
        AssertEx.Equal(SandboxNetworkPolicy.None, sandbox.Created[0].NetworkPolicy);
    }

    /// <summary>
    ///     The other direction, asserted with the capability flipped rather than inferred. A backend fails a
    ///     confinement request it cannot honour CLOSED, so an unconditional <c>None</c> would not harden Development
    ///     Mode on such a node — it would remove Development Mode from it. What keeps that from being silent is the
    ///     Development status surface, which reports the posture the provider actually served.
    /// </summary>
    [Test]
    public async Task PrepareAsync_OnABackendWithoutNetworkPolicy_FallsBackToUnrestrictedRatherThanFailingClosed()
    {
        var sandbox = new CapabilitySandbox(SandboxProviderCapabilities.SupportsTrustedHostWorkspace
                                            | SandboxProviderCapabilities.SupportsKill);

        _ = await PrepareAsync(sandbox).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, sandbox.Created.Count);
        AssertEx.Equal(SandboxNetworkPolicy.Unrestricted, sandbox.Created[0].NetworkPolicy);
    }

    /// <summary>
    ///     The live half, on the backend Development Mode actually runs on today, and the only form of this claim worth
    ///     having: a <c>dotnet restore</c> inside an agent-facing sandbox FAILS with no network and no warmed cache,
    ///     and the same profile's restore, build and test all succeed inside an equally denied sandbox once the warm
    ///     has run. Without the first half the second proves only that a cache existed; without the second, only that
    ///     the feature is broken.
    /// </summary>
    [Test]
    public async Task AgentFacingSandbox_WithoutNetwork_FailsAColdRestoreAndPassesTheWholeProfileAgainstTheWarmedCache()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip("egress denial on the process backend is a Linux mechanism (an empty network namespace via unshare).");
            return;
        }

        using var probe = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), TimeProvider.System);
        if (!probe.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy))
        {
            Skip("this host cannot deny egress, so Development Mode correctly stays Unrestricted and there is nothing to exercise.");
            return;
        }

        if (!await NuGetIsReachableAsync().ConfigureAwait(false))
        {
            Skip("this box cannot reach api.nuget.org, so a warm restore has nothing to warm FROM and the comparison is vacuous.");
            return;
        }

        Directory.CreateDirectory(_root);
        var repository = Path.Combine(_root, "solution");
        await DevelopmentSyntheticSolutionRepository.CreateAsync(repository, includeTests: true).ConfigureAwait(false);

        // The shared fixture is deliberately OFFLINE — it clears package sources and declares the host package cache
        // as a fallback folder, so its restore succeeds with no network by construction. That is right for every other
        // test and useless for this one: it would report a green no-egress restore whether or not a warm had ever run.
        // Removing the file puts the repository back on the machine's real sources, which is the shape of an actual
        // registered repository and the only shape in which "the warm is what made this work" is falsifiable.
        File.Delete(Path.Combine(repository, "NuGet.config"));
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "rm", "--quiet", "--", "NuGet.config").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "commit", "-m", "restore from the machine's real package sources").ConfigureAwait(false);

        var worktree = Path.Combine(_root, "worktree");
        await CloneDetachedAsync(repository, worktree).ConfigureAwait(false);

        // The fixture's committed baseline deliberately FAILS its own test, so that every coder attempt produces a
        // non-empty diff. This test is about egress, not about the gate's verdict, so it stands in for the coder by
        // writing the passing implementation — otherwise `dotnet test` would exit non-zero for a reason that has
        // nothing to do with the network.
        await File.WriteAllTextAsync(Path.Combine(worktree, DevelopmentSyntheticSolutionRepository.LibrarySourcePath.Replace('/', Path.DirectorySeparatorChar)),
                      DevelopmentSyntheticSolutionRepository.PassingLibrarySource)
                  .ConfigureAwait(false);

        var profile = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx,
            DevelopmentSyntheticSolutionRepository.SolutionPath);
        var head = await ReadGitOutputAsync(worktree, "rev-parse", "--verify", "HEAD^{commit}").ConfigureAwait(false);

        // COLD: a per-task package root with nothing in it, and a sandbox with no egress. The restore has to fail —
        // if it does not, this host resolves packages from somewhere the test did not control and every other
        // assertion below would be measuring that instead.
        var cold = Path.Combine(_root, "runtime-cold");
        var coldEvidence = await RunAsync(probe,
                worktree,
                cold,
                head,
                profile,
                SandboxNetworkPolicy.None,
                DevelopmentCommandIds.DotnetRestore)
            .ConfigureAwait(false);
        AssertEx.NotEqual(notExpected: 0,
            coldEvidence[^1].ExitCode,
            "a cold restore inside a sandbox with no egress must FAIL; it apparently reached packages from somewhere: "
            + coldEvidence[^1].StandardOutput);

        // WARM: exactly what DevelopmentWorkspaceProvider does before it creates the agent-facing sandbox — the same
        // command, the same runtime roots, with egress.
        var warm = Path.Combine(_root, "runtime-warm");
        var warmEvidence = await RunAsync(probe,
                worktree,
                warm,
                head,
                profile,
                SandboxNetworkPolicy.Unrestricted,
                DevelopmentCommandIds.DotnetRestore)
            .ConfigureAwait(false);
        AssertEx.Equal(expected: 0, warmEvidence[^1].ExitCode, warmEvidence[^1].StandardOutput + warmEvidence[^1].StandardError);

        // AGENT-FACING: the whole validation profile, denied egress, against the cache the warm left behind.
        var validated = await RunAsync(probe,
                worktree,
                warm,
                head,
                profile,
                SandboxNetworkPolicy.None,
                [.. profile.ValidationCommandIds])
            .ConfigureAwait(false);
        foreach (var command in validated)
        {
            AssertEx.Equal(expected: 0,
                command.ExitCode,
                $"{command.CommandId} must succeed with no egress against the warmed cache: {command.StandardOutput}{command.StandardError}");
        }

        AssertEx.Contains(validated.Select(static command => command.CommandId), DevelopmentCommandIds.DotnetBuildRelease);
        AssertEx.Contains(validated.Select(static command => command.CommandId), DevelopmentCommandIds.DotnetTestRelease);
    }

    /// <summary>
    ///     Whether a warm restore could actually fetch anything from this box. Without it the cold/warm comparison
    ///     below compares two failures and asserts nothing.
    /// </summary>
    private static async Task<bool> NuGetIsReachableAsync()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await client.ConnectAsync("api.nuget.org", 443, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Runs the named commands in one sandbox created with <paramref name="networkPolicy" />, through the same
    ///     tools an attempt uses, so the environment and the argument vectors are the production ones rather than a
    ///     restatement of them.
    /// </summary>
    private static async Task<IReadOnlyList<DevelopmentCommandEvidence>> RunAsync(ProcessSandboxRuntimeProvider sandbox,
        string worktree,
        string runtimePath,
        string baseCommit,
        DevelopmentCommandProfile profile,
        SandboxNetworkPolicy networkPolicy,
        params string[] commandIds)
    {
        foreach (var name in new[]
                 {
                     "home",
                     "tmp",
                     "nuget",
                     "dotnet"
                 })
        {
            Directory.CreateDirectory(Path.Combine(runtimePath, name));
        }

        var handle = await sandbox.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = Guid.NewGuid().ToString("N"),
                NodeId = Guid.NewGuid().ToString("N"),
                ProviderName = sandbox.ProviderName,
                RuntimeProfile = networkPolicy == SandboxNetworkPolicy.None ? "development-local" : "development-warm",
                ManifestVersion = 2
            },
            RuntimeProfile = networkPolicy == SandboxNetworkPolicy.None ? "development-local" : "development-warm",
            NetworkPolicy = networkPolicy,
            TrustedHostWorkspace = new SandboxTrustedHostWorkspace
            {
                RootPath = worktree
            },
            Mounts =
            [
                .. new[]
                {
                    "home",
                    "tmp",
                    "nuget",
                    "dotnet"
                }.Select(name => new SandboxMount
                {
                    HostPath = Path.Combine(runtimePath, name),
                    SandboxPath = "/xe-runtime/" + name,
                    ReadOnly = false
                })
            ]
        }).ConfigureAwait(false);

        try
        {
            var session = new DevelopmentWorkspaceSession(Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                baseCommit,
                "identity",
                worktree,
                runtimePath,
                handle);
            var tools = new DevelopmentWorkspaceTools(sandbox, session, Options.Create(OptionsValue()), profile);
            foreach (var commandId in commandIds)
            {
                _ = await tools.RunCommandAsync(commandId).ConfigureAwait(false);
            }

            return [.. tools.CommandEvidence];
        }
        finally
        {
            await sandbox.KillAsync(handle).ConfigureAwait(false);
        }
    }

    private async Task<DevelopmentWorkspaceSession> PrepareAsync(CapabilitySandbox sandbox)
    {
        Directory.CreateDirectory(_root);
        var repository = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repository);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "init", "--initial-branch=main", ".").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "config", "user.email", "development-egress@example.invalid").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "config", "user.name", "Development Egress Test").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "base\n").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "add", "README.md").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "commit", "-m", "base").ConfigureAwait(false);

        var data = Path.Combine(_root, "d-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(data);
        var identity = DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository));
        var snapshot = Snapshot(identity);
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, Options.Create(OptionsValue()), TimeProvider.System, Substitute.For<IDevelopmentStore>());
        return await provider.PrepareAsync(snapshot,
                new DevelopmentRepositoryBinding(snapshot.ProjectId,
                    snapshot.SelectedFolderId!.Value,
                    "repository",
                    repository,
                    identity))
            .ConfigureAwait(false);
    }

    private static async Task CloneDetachedAsync(string repository, string worktree)
    {
        Directory.CreateDirectory(worktree);
        await DevelopmentMountBrokerTests.RunGitAsync(Path.GetDirectoryName(worktree)!, "clone", "--no-hardlinks", repository, worktree).ConfigureAwait(false);
        var head = await ReadGitOutputAsync(worktree, "rev-parse", "--verify", "HEAD^{commit}").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(worktree, "checkout", "--detach", head).ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(worktree, "remote", "remove", "origin").ConfigureAwait(false);
    }

    private static async Task<string> ReadGitOutputAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
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
        var error = process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 0, process.ExitCode, await error.ConfigureAwait(false));
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
            MaxAttemptDurationSeconds = 600,
            MaxOutputTokens = 2048
        };

    private static DevelopmentExecutionSnapshot Snapshot(string identity) =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            identity,
            "main",
            DevelopmentEgressPolicy.LocalOnly,
            ConfigurationVersion: 1,
            TrustedRepositoryAcknowledged: true,
            DevelopmentTrustPolicy.CurrentVersion,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MaxTokens: 2048,
            MaxDurationSeconds: 600,
            "Implement feature",
            "Add the bounded feature file.",
            "[\"feature.txt exists\"]",
            DevelopmentTaskStatus.InProgress,
            TaskVersion: 3,
            DevelopmentAttemptRole.Coder,
            PersistenceDevelopmentAttemptStatus.Running,
            "local-model",
            "local",
            AttemptVersion: 1,

            // generic-git declares no restore command, so no warm sandbox is created and the only create request
            // recorded is the agent-facing one this test is about.
            Encoding.UTF8.GetString(GenericProfile.ToCanonicalUtf8()));

    /// <summary>
    ///     Marks a live-gated test SKIPPED with the measured reason rather than returning green. A containment test
    ///     that silently passes on a host without the mechanism reports that egress denial works when nothing was
    ///     exercised at all.
    /// </summary>
    private static void Skip(string reason)
    {
        throw new SkipTestException(reason);
    }

    /// <summary>A double whose only interesting property is which capabilities it advertises.</summary>
    private sealed class CapabilitySandbox(SandboxProviderCapabilities capabilities) : IDevelopmentSandboxRuntimeProvider
    {
        private readonly List<SandboxCreateRequest> _created = [];

        public IReadOnlyList<SandboxCreateRequest> Created => _created;

        public string ProviderName => "capability";

        public SandboxProviderCapabilities Capabilities => capabilities;

        public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
        {
            _created.Add(request);
            return Task.FromResult(new SandboxHandle
            {
                ProviderName = ProviderName,
                SandboxId = "capability-sandbox",
                AttachKey = request.AttachKey,
                CreatedAt = DateTimeOffset.UnixEpoch,
                ManifestVersion = request.AttachKey.ManifestVersion,
                Mounts = [.. (request.Mounts ?? []).Select(static mount => new SandboxMountBinding(mount.HostPath, mount.SandboxPath, mount.ReadOnly))]
            });
        }

        public Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default) =>
            throw new SandboxHandleInvalidException("nothing to attach to");

        public Task<SandboxCommandResult> ExecuteAsync(SandboxHandle handle, SandboxCommandRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("this fixture creates a sandbox and runs nothing in it.");

        public Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> ReadFileAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task CopyOutAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
