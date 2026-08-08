namespace XE_Local_AI_Engine.Client.Services.Development;

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

internal sealed class TrustedDevelopmentHostApplyPort(
    IDevelopmentArtifactBlobStore blobStore,
    IOptions<DevelopmentOptions> options) : IDevelopmentHostApplyPort
{
    private readonly IDevelopmentArtifactBlobStore _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    private readonly DevelopmentOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var state = await ResolveAsync(subject, repositoryRoot, cancellationToken).ConfigureAwait(false);
        return state.State;
    }

    public async Task ApplyAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var before = await ResolveAsync(subject, repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (before.State != DevelopmentHostApplyState.UnappliedBaseUnchanged)
        {
            throw new DevelopmentInvalidTransitionException("The trusted host repository is not at the exact approved unapplied base.");
        }

        var apply = await RunGitAsync(before.RepositoryRoot,
            ["apply", "--index", "--whitespace=error-all", "-"],
            before.Patch,
            cancellationToken).ConfigureAwait(false);
        if (apply.ExitCode != 0)
        {
            throw new InvalidOperationException("The exact approved Development patch could not be applied to the trusted host repository.");
        }

        var after = await ResolveAsync(subject, repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (after.State != DevelopmentHostApplyState.ExactApprovedResultPresent)
        {
            throw new InvalidOperationException("The trusted host repository did not reach the exact approved result after apply.");
        }
    }

    private async Task<ResolvedApplyState> ResolveAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        ValidateSubject(subject);
        var canonicalRoot = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repositoryRoot);
        if (!string.Equals(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(canonicalRoot),
                subject.RepositoryIdentityHash,
                StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedApplyState(DevelopmentHostApplyState.Ambiguous, canonicalRoot, ReadOnlyMemory<byte>.Empty);
        }

        var patch = await ReadArtifactAsync(subject.ProjectId,
            subject.PatchArtifactId,
            subject.PatchArtifactReference,
            subject.PatchHash,
            subject.PatchByteCount,
            cancellationToken).ConfigureAwait(false);
        _ = await ReadArtifactAsync(subject.ProjectId,
            subject.ManifestArtifactId,
            subject.ManifestArtifactReference,
            subject.ManifestHash,
            subject.ManifestByteCount,
            cancellationToken).ConfigureAwait(false);

        var topLevel = await RunGitAsync(canonicalRoot, ["rev-parse", "--show-toplevel"], null, cancellationToken).ConfigureAwait(false);
        var branch = await RunGitAsync(canonicalRoot, ["symbolic-ref", "--quiet", "--short", "HEAD"], null, cancellationToken).ConfigureAwait(false);
        var head = await RunGitAsync(canonicalRoot, ["rev-parse", "--verify", "HEAD^{commit}"], null, cancellationToken).ConfigureAwait(false);
        if (topLevel.ExitCode != 0
            || branch.ExitCode != 0
            || head.ExitCode != 0
            || !PathEquals(canonicalRoot, topLevel.StandardOutputText.Trim())
            || !string.Equals(branch.StandardOutputText.Trim(), subject.BaseBranch, StringComparison.Ordinal)
            || !string.Equals(head.StandardOutputText.Trim(), subject.BaseCommit, StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedApplyState(DevelopmentHostApplyState.Ambiguous, canonicalRoot, patch);
        }

        var resultTree = await RunGitAsync(canonicalRoot, ["write-tree"], null, cancellationToken).ConfigureAwait(false);
        var unstaged = await RunGitAsync(canonicalRoot, ["diff", "--quiet", "--", "."], null, cancellationToken).ConfigureAwait(false);
        if (resultTree.ExitCode == 0
            && unstaged.ExitCode == 0
            && string.Equals(resultTree.StandardOutputText.Trim(), subject.ExpectedResultHash, StringComparison.OrdinalIgnoreCase))
        {
            var appliedPatch = await RunGitAsync(canonicalRoot,
                ["diff", "--cached", "--binary", "--full-index", "--no-ext-diff", "HEAD", "--", "."],
                null,
                cancellationToken).ConfigureAwait(false);
            if (appliedPatch.ExitCode == 0
                && string.Equals(Hash(appliedPatch.StandardOutput), subject.PatchHash, StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedApplyState(DevelopmentHostApplyState.ExactApprovedResultPresent, canonicalRoot, patch);
            }
        }

        var status = await RunGitAsync(canonicalRoot, ["status", "--porcelain=v1", "--untracked-files=all"], null, cancellationToken).ConfigureAwait(false);
        if (status.ExitCode != 0 || status.StandardOutput.Length != 0)
        {
            return new ResolvedApplyState(DevelopmentHostApplyState.Ambiguous, canonicalRoot, patch);
        }

        var check = await RunGitAsync(canonicalRoot,
            ["apply", "--check", "--whitespace=error-all", "-"],
            patch,
            cancellationToken).ConfigureAwait(false);
        return new ResolvedApplyState(check.ExitCode == 0
                ? DevelopmentHostApplyState.UnappliedBaseUnchanged
                : DevelopmentHostApplyState.Ambiguous,
            canonicalRoot,
            patch);
    }

    private async Task<ReadOnlyMemory<byte>> ReadArtifactAsync(Guid projectId,
        Guid artifactId,
        string opaqueReference,
        string expectedHash,
        long expectedByteCount,
        CancellationToken cancellationToken)
    {
        var expectedReference = string.Concat(projectId.ToString("N"), "/", artifactId.ToString("N"));
        if (!string.Equals(opaqueReference, expectedReference, StringComparison.Ordinal))
        {
            throw new DevelopmentInvalidTransitionException("The approved artifact reference is not the engine-owned opaque key.");
        }

        var read = await _blobStore.ReadAsync(projectId,
            artifactId,
            expectedHash,
            expectedByteCount,
            cancellationToken).ConfigureAwait(false);
        if (read.Status != DevelopmentArtifactReadStatus.Found)
        {
            throw new DevelopmentInvalidTransitionException($"The approved artifact failed immutable verification ({read.Status}).");
        }

        return read.Content;
    }

    private async Task<GitBytesResult> RunGitAsync(string workingDirectory,
        IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte>? standardInput,
        CancellationToken cancellationToken)
    {
#pragma warning disable S4036 // Git is the code-owned executable and must resolve cross-platform through the controlled PATH.
        var startInfo = new ProcessStartInfo
        {
            FileName = AgentHomeGit.Executable,
#pragma warning restore S4036
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Prefixed with the SAME hardened `-c` vector DevelopmentPatchEvidenceService runs under, and it was missing
        // here. Two consequences, and the second is the one that bites first.
        //
        // Byte drift: the evidence service computes the approved PatchHash from `diff --cached --binary` taken WITH
        // core.attributesfile=/dev/null and core.quotePath=false, and this port recomputes the same diff and compares
        // hashes. A `.gitattributes` in the trusted repository defining a clean filter or a text conversion made the
        // two disagree, and the apply then reported "did not reach the exact approved result" for a reason that had
        // nothing to do with the patch.
        //
        // Exec suppression: `status` refreshes the index, so a repository-local core.fsmonitor executes here. This is
        // the operator's OWN repository rather than an agent-writable one, so it is defence in depth rather than a
        // closed hole — but it costs nothing and it stops a later command re-opening it.
        //
        // NOTE this port runs against `repositoryRoot`, the operator's registered repository — NOT the managed
        // workspace. The engine-side .git/config REWRITE therefore deliberately does not run here: it would rewrite the
        // user's own repository configuration, which the engine does not own and the agent cannot reach.
        foreach (var argument in AgentHomeGit.Arguments([.. arguments]))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.MaxAttemptDurationSeconds));
        using var process = new Process
        {
            StartInfo = startInfo
        };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The trusted Development host Git command could not be started.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("The trusted Development host Git command could not be started.", exception);
        }

        try
        {
            if (standardInput is { } input)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, _options.MaxPatchBytes, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError.BaseStream, _options.MaxCommandOutputBytes, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new GitBytesResult(process.ExitCode,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false));
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        using var output = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maxBytes)
            {
                throw new InvalidDataException("The trusted Development host Git output exceeded its configured bound.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateSubject(DevelopmentApprovedApplySubject subject)
    {
        if (subject.PatchArtifactId == Guid.Empty
            || subject.ManifestArtifactId == Guid.Empty
            || string.IsNullOrWhiteSpace(subject.SubjectHash)
            || string.IsNullOrWhiteSpace(subject.RepositoryIdentityHash)
            || string.IsNullOrWhiteSpace(subject.BaseBranch)
            || string.IsNullOrWhiteSpace(subject.ExpectedResultHash)
            || subject.PatchByteCount <= 0
            || subject.ManifestByteCount <= 0)
        {
            throw new DevelopmentInvalidTransitionException("The approved apply subject is incomplete.");
        }
    }

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content));

    private static bool PathEquals(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
        catch (Win32Exception)
        {
            // Best-effort cleanup preserves the original failure.
        }
    }

    private sealed record ResolvedApplyState(
        DevelopmentHostApplyState State,
        string RepositoryRoot,
        ReadOnlyMemory<byte> Patch);

    private sealed record GitBytesResult(int ExitCode, byte[] StandardOutput, byte[] StandardError)
    {
        public string StandardOutputText => Encoding.UTF8.GetString(StandardOutput);
    }
}
