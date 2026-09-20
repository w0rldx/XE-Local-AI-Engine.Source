namespace XE_Local_AI_Engine.Client.Services.Development;

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

internal sealed class TrustedDevelopmentHostApplyPort : IDevelopmentHostApplyPort
{
    private readonly IDevelopmentArtifactBlobStore _blobStore;
    private readonly DevelopmentOptions _options;

    public TrustedDevelopmentHostApplyPort(
        IDevelopmentArtifactBlobStore blobStore,
        IOptions<DevelopmentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(blobStore);
        _blobStore = blobStore;
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var state = await ResolveAsync(subject, repositoryRoot, cancellationToken);
        return state.State;
    }

    public async Task ApplyAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var before = await ResolveAsync(subject, repositoryRoot, cancellationToken);
        if (before.State != DevelopmentHostApplyState.UnappliedBaseUnchanged)
        {
            throw new DevelopmentInvalidTransitionException("The trusted host repository is not at the exact approved unapplied base.");
        }

        var apply = await RunGitAsync(before.RepositoryRoot,
            ["apply", "--index", "--whitespace=error-all", "-"],
            before.Patch,
            cancellationToken);
        if (apply.ExitCode != 0)
        {
            throw new InvalidOperationException("The exact approved Development patch could not be applied to the trusted host repository.");
        }

        var after = await ResolveAsync(subject, repositoryRoot, cancellationToken);
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
            return new ResolvedApplyState { State = DevelopmentHostApplyState.Ambiguous, RepositoryRoot = canonicalRoot, Patch = ReadOnlyMemory<byte>.Empty };
        }

        var patch = await ReadArtifactAsync(subject.ProjectId,
            subject.PatchArtifactId,
            subject.PatchArtifactReference,
            subject.PatchHash,
            subject.PatchByteCount,
            cancellationToken);
        _ = await ReadArtifactAsync(subject.ProjectId,
            subject.ManifestArtifactId,
            subject.ManifestArtifactReference,
            subject.ManifestHash,
            subject.ManifestByteCount,
            cancellationToken);

        var topLevel = await RunGitAsync(canonicalRoot, ["rev-parse", "--show-toplevel"], null, cancellationToken);
        var branch = await RunGitAsync(canonicalRoot, ["symbolic-ref", "--quiet", "--short", "HEAD"], null, cancellationToken);
        var head = await RunGitAsync(canonicalRoot, ["rev-parse", "--verify", "HEAD^{commit}"], null, cancellationToken);
        if (topLevel.ExitCode != 0
            || branch.ExitCode != 0
            || head.ExitCode != 0
            || !PathEquals(canonicalRoot, topLevel.StandardOutputText.Trim())
            || !string.Equals(branch.StandardOutputText.Trim(), subject.BaseBranch, StringComparison.Ordinal)
            || !string.Equals(head.StandardOutputText.Trim(), subject.BaseCommit, StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedApplyState { State = DevelopmentHostApplyState.Ambiguous, RepositoryRoot = canonicalRoot, Patch = patch };
        }

        var resultTree = await RunGitAsync(canonicalRoot, ["write-tree"], null, cancellationToken);
        var unstaged = await RunGitAsync(canonicalRoot, ["diff", "--quiet", "--", "."], null, cancellationToken);
        if (resultTree.ExitCode == 0
            && unstaged.ExitCode == 0
            && string.Equals(resultTree.StandardOutputText.Trim(), subject.ExpectedResultHash, StringComparison.OrdinalIgnoreCase))
        {
            var appliedPatch = await RunGitAsync(canonicalRoot,
                ["diff", "--cached", "--binary", "--full-index", "--no-ext-diff", "HEAD", "--", "."],
                null,
                cancellationToken);
            if (appliedPatch.ExitCode == 0
                && string.Equals(Hash(appliedPatch.StandardOutput), subject.PatchHash, StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedApplyState { State = DevelopmentHostApplyState.ExactApprovedResultPresent, RepositoryRoot = canonicalRoot, Patch = patch };
            }
        }

        var status = await RunGitAsync(canonicalRoot, ["status", "--porcelain=v1", "--untracked-files=all"], null, cancellationToken);
        if (status.ExitCode != 0 || status.StandardOutput.Length != 0)
        {
            return new ResolvedApplyState { State = DevelopmentHostApplyState.Ambiguous, RepositoryRoot = canonicalRoot, Patch = patch };
        }

        var check = await RunGitAsync(canonicalRoot,
            ["apply", "--check", "--whitespace=error-all", "-"],
            patch,
            cancellationToken);
        return new ResolvedApplyState
        {
            State = check.ExitCode == 0
                ? DevelopmentHostApplyState.UnappliedBaseUnchanged
                : DevelopmentHostApplyState.Ambiguous,
            RepositoryRoot = canonicalRoot,
            Patch = patch
        };
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
            cancellationToken);
        if (read.Status != DevelopmentArtifactReadStatus.Found)
        {
            throw new DevelopmentInvalidTransitionException($"The approved artifact failed immutable verification ({read.Status}).");
        }

        return read.Content;
    }

    /// <summary>
    ///     Runs one Git command under the same hardened <c>-c</c> vector <c>DevelopmentPatchEvidenceService</c> uses.
    /// </summary>
    /// <remarks>
    ///     The approved <c>PatchHash</c> comes from <c>diff --cached --binary</c> taken with
    ///     <c>core.attributesfile=/dev/null</c> and <c>core.quotePath=false</c>, and this port recomputes it, so a
    ///     <c>.gitattributes</c> defining a clean filter or a text conversion makes the two disagree and the apply
    ///     reports a result mismatch that has nothing to do with the patch. The pins also stop a repository-local
    ///     <c>core.fsmonitor</c> executing on the index refresh <c>status</c> performs.
    /// </remarks>
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
        // This port runs against repositoryRoot, the operator's registered repository, not the managed workspace, so
        // the engine-side .git/config rewrite deliberately does not run here. See this method's remarks for the pins.
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
                await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token);
                process.StandardInput.Close();
            }

            var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, _options.MaxPatchBytes, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError.BaseStream, _options.MaxCommandOutputBytes, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new GitBytesResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = await outputTask,
                StandardError = await errorTask
            };
        }
        catch
        {
            ProcessTermination.TryKill(process);
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        using var output = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maxBytes)
            {
                throw new InvalidDataException("The trusted Development host Git output exceeded its configured bound.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
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

    private sealed record ResolvedApplyState
    {
        public required DevelopmentHostApplyState State { get; init; }

        public required string RepositoryRoot { get; init; }

        public required ReadOnlyMemory<byte> Patch { get; init; }
    }

    private sealed record GitBytesResult
    {
        public required int ExitCode { get; init; }

        public required byte[] StandardOutput { get; init; }

        public required byte[] StandardError { get; init; }

        public string StandardOutputText => Encoding.UTF8.GetString(StandardOutput);
    }
}
