namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The apply port's host-side Git invocations, which ran WITHOUT the hardened <c>-c</c> vector that every other
///     host-side Git path in Development Mode uses.
///     <para>
///         Note what this port operates on, because it changes what the right fix is: <c>repositoryRoot</c> is the
///         operator's own registered repository, NOT the managed workspace. The engine-side <c>.git/config</c> rewrite
///         therefore must not run here — it would rewrite configuration the engine does not own and the agent cannot
///         reach. Pinning the exec-bearing keys is the correct closure for this path, and it also removes a byte-drift
///         hazard: the approved <c>PatchHash</c> is computed by the evidence service WITH those pins, and this port
///         recomputes the same diff and compares hashes.
///     </para>
/// </summary>
public sealed class TrustedDevelopmentHostApplyPortHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-apply-port-hardening-" + Guid.NewGuid().ToString("N"));

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
    public async Task InspectAsync_DoesNotExecuteARepositoryLocalFsmonitorCommand()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new SkipTestException("SKIPPED — the payload is a POSIX shell script. This pin runs on Linux and macOS.");
        }

        var repository = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repository);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "init", "--initial-branch=main", ".").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "config", "user.email", "apply@example.invalid").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "config", "user.name", "Apply Port Test").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "base\n").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "add", "README.md").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "commit", "-m", "base").ConfigureAwait(false);

        var sentinel = Path.Combine(_root, "SENTINEL");
        var payload = Path.Combine(_root, "payload.sh");
        await File.WriteAllTextAsync(payload, $"#!/bin/sh\ntouch \"{sentinel}\"\nexit 1\n").ConfigureAwait(false);
        File.SetUnixFileMode(payload, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.AppendAllTextAsync(Path.Combine(repository, ".git", "config"),
            $"\n[core]\n\tfsmonitor = {payload}\n").ConfigureAwait(false);

        var blobStore = Substitute.For<IDevelopmentArtifactBlobStore>();
        blobStore.ReadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                 .Returns(new DevelopmentArtifactBlobReadResult(DevelopmentArtifactReadStatus.Found, new byte[]
                 {
                     1,
                     2,
                     3
                 }));

        var port = new TrustedDevelopmentHostApplyPort(blobStore, Options.Create(OptionsValue()));
        var projectId = Guid.NewGuid();
        var patchArtifactId = Guid.NewGuid();
        var manifestArtifactId = Guid.NewGuid();

        // The state the port returns is not what is under test — reaching the Git commands is. An index-refreshing
        // command (`write-tree` / `status`) is what fires fsmonitor, and every path through ResolveAsync runs one once
        // the identity hash matches.
        _ = await port.InspectAsync(new DevelopmentApprovedApplySubject(projectId,
                              Guid.NewGuid(),
                              ExpectedTaskVersion: 1,
                              await ReadHeadAsync(repository).ConfigureAwait(false),
                              "PATCHHASH",
                              "MANIFESTHASH",
                              "RESULTHASH",
                              $"{projectId:N}/{patchArtifactId:N}",
                              $"{projectId:N}/{manifestArtifactId:N}",
                              patchArtifactId,
                              manifestArtifactId,
                              "SUBJECTHASH",
                              DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)),
                              "main",
                              PatchByteCount: 3,
                              ManifestByteCount: 3),
                          repository)
                      .ConfigureAwait(false);

        AssertEx.False(File.Exists(sentinel), "the repository-local core.fsmonitor command executed on the host.");
    }

    private static async Task<string> ReadHeadAsync(string repository)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repository,
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
}
