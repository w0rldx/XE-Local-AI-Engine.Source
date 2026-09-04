namespace XE_Local_AI_Engine.Client.Services.Development;

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

internal interface IDevelopmentPatchEvidenceService
{
    Task<DevelopmentPatchEvidence> ExportAsync(DevelopmentWorkspaceSession session, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> ListChangedPathsAsync(DevelopmentWorkspaceSession session, CancellationToken cancellationToken = default);
}

/// <summary>
///     Exports the final patch subject for a Development attempt.
///     <para>
///         Takes NO sandbox provider, deliberately. Every Git command below runs on the HOST against the managed
///         worktree (see <see cref="RunGitExactAsync" />) rather than inside the attempt's sandbox, because the subject
///         hash the operator approves must be produced by the engine's own trusted Git, not by whatever the sandboxed
///         attempt could influence. It carried an unused <c>ISandboxRuntimeProvider</c> parameter until the per-feature
///         seam landed; that only ever advertised a dependency this service does not have.
///     </para>
/// </summary>
internal sealed class DevelopmentPatchEvidenceService : IDevelopmentPatchEvidenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DevelopmentOptions _options;

    public DevelopmentPatchEvidenceService(IOptions<DevelopmentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async Task<DevelopmentPatchEvidence> ExportAsync(DevelopmentWorkspaceSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        await StageWorkingTreeAsync(session, cancellationToken).ConfigureAwait(false);
        var patch = await RunGitExactAsync(session,
            ["diff", "--cached", "--binary", "--full-index", "--no-ext-diff", "HEAD", "--", "."],
            maxOutputBytes: _options.MaxPatchBytes,
            cancellationToken).ConfigureAwait(false);
        var status = await RunGitExactAsync(session,
            ["diff", "--cached", "--name-status", "-z", "HEAD", "--", "."],
            maxOutputBytes: _options.MaxPatchBytes,
            cancellationToken).ConfigureAwait(false);
        var resultTree = await RunGitExactAsync(session,
            ["write-tree"],
            maxOutputBytes: 4096,
            cancellationToken).ConfigureAwait(false);

        var patchBytes = patch.StandardOutput;
        if (patchBytes.Length == 0 || patchBytes.Length > _options.MaxPatchBytes)
        {
            throw new InvalidOperationException("The final Development patch is empty or exceeds the configured patch limit.");
        }

        var changedFiles = ParseStatus(status.StandardOutput);
        if (changedFiles.Count == 0 || changedFiles.Count > _options.MaxChangedFiles)
        {
            throw new InvalidOperationException("The final Development changed-file manifest is empty or exceeds the configured file limit.");
        }

        foreach (var item in changedFiles)
        {
            var confined = DevelopmentWorkspaceSecurity.Confine(item.Path, allowRoot: false);
            if (!confined.IsAccepted)
            {
                throw new DevelopmentWorkspaceSecurityException("The final patch contains a protected or escaping path.");
            }

            if (item.PreviousPath is not null && !DevelopmentWorkspaceSecurity.Confine(item.PreviousPath, allowRoot: false).IsAccepted)
            {
                throw new DevelopmentWorkspaceSecurityException("The final patch contains a protected or escaping source path.");
            }
        }

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(changedFiles.OrderBy(static item => item.Path, StringComparer.Ordinal), JsonOptions);
        var patchHash = Hash(patchBytes);
        var manifestHash = Hash(manifestBytes);
        var subjectHash = Hash(Encoding.UTF8.GetBytes(string.Concat("xe-development-subject/v1\0",
            session.BaseCommit,
            "\0",
            patchHash,
            "\0",
            manifestHash,
            "\0")));
        return new DevelopmentPatchEvidence(session.BaseCommit,
            patchHash,
            manifestHash,
            subjectHash,
            Encoding.UTF8.GetString(resultTree.StandardOutput).Trim(),
            patchBytes,
            manifestBytes,
            changedFiles);
    }

    /// <summary>
    ///     The workspace paths that differ from the base commit right now. Shares its staging and its
    ///     <c>--name-status</c> parse with <see cref="ExportAsync" />, so the two can never disagree about how a path
    ///     is spelled. Unlike the export it tolerates an empty result, which is the ordinary state of a task's first
    ///     attempt; its only size bound is <c>MaxPatchBytes</c> on the <c>--name-status</c> output, which
    ///     <see cref="RunGitExactAsync" /> enforces.
    /// </summary>
    public async Task<IReadOnlySet<string>> ListChangedPathsAsync(DevelopmentWorkspaceSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        await StageWorkingTreeAsync(session, cancellationToken).ConfigureAwait(false);
        var status = await RunGitExactAsync(session,
            ["diff", "--cached", "--name-status", "-z", "HEAD", "--", "."],
            maxOutputBytes: _options.MaxPatchBytes,
            cancellationToken).ConfigureAwait(false);
        return status.StandardOutput.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)

            // Both ends of a rename: an attempt that renames b back to a has changed a, and a caller comparing a
            // submission against this set would otherwise refuse the one shape it exists to forgive. Protected and
            // escaping paths are dropped silently — ExportAsync judges those at the end of the attempt, as it does now.
            : ParseStatus(status.StandardOutput)
              .SelectMany(static item => item.PreviousPath is null ? (string[])[item.Path] : [item.Path, item.PreviousPath])
              .Where(static path => DevelopmentWorkspaceSecurity.Confine(path, allowRoot: false).IsAccepted)
              .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    ///     Refreshes the index so a <c>--cached</c> diff against HEAD sees the whole working tree, untracked files
    ///     included.
    ///     <para>
    ///         The config restore sits immediately before the first HOST-side Git command, and the ordering is the
    ///         control. <c>reset</c> refreshes the index and <c>add -A</c> runs clean filters, so a repository-local
    ///         <c>core.fsmonitor</c> or <c>filter.&lt;driver&gt;.clean</c> in the workspace's own .git/config executes
    ///         HERE, on the machine running the engine — and the standalone clone is what made that file
    ///         agent-writable inside the jail. AgentHomeGit's -c pins close fsmonitor but cannot close filter drivers,
    ///         whose names are arbitrary; removing the definitions closes both without enumerating any key. No
    ///         command of THIS attempt is in flight at either caller: the export runs after the attempt finished, and
    ///         the changed-path listing runs before the model starts. The sandbox is per task and outlives an attempt,
    ///         so a process a previous attempt leaked can still be writing here.
    ///     </para>
    /// </summary>
    private async Task StageWorkingTreeAsync(DevelopmentWorkspaceSession session, CancellationToken cancellationToken)
    {
        DevelopmentWorkspaceGitConfig.RestoreMinimal(session.HostWorktreePath);
        _ = await RunGitExactAsync(session,
            ["reset", "--mixed", "--quiet", "HEAD", "--"],
            maxOutputBytes: 4096,
            cancellationToken).ConfigureAwait(false);
        _ = await RunGitExactAsync(session,
            ["add", "-A", "--", "."],
            maxOutputBytes: 4096,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExactGitResult> RunGitExactAsync(DevelopmentWorkspaceSession session,
        IReadOnlyList<string> tail,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(session.RuntimePath, "home"));
        Directory.CreateDirectory(Path.Combine(session.RuntimePath, "tmp"));
#pragma warning disable S4036 // Git is the code-owned executable and must resolve cross-platform through the controlled PATH.
        var startInfo = new ProcessStartInfo
        {
            FileName = AgentHomeGit.Executable,
#pragma warning restore S4036
            WorkingDirectory = session.HostWorktreePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in AgentHomeGit.Arguments([.. tail]))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        CopyEnvironment(startInfo, "PATH");
        CopyEnvironment(startInfo, "SystemRoot");
        CopyEnvironment(startInfo, "windir");
        startInfo.Environment["HOME"] = Path.Combine(session.RuntimePath, "home");
        startInfo.Environment["TMPDIR"] = Path.Combine(session.RuntimePath, "tmp");
        startInfo.Environment["TMP"] = Path.Combine(session.RuntimePath, "tmp");
        startInfo.Environment["TEMP"] = Path.Combine(session.RuntimePath, "tmp");
        startInfo.Environment["LC_ALL"] = "C";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.MaxAttemptDurationSeconds));
        using var process = new Process
        {
            StartInfo = startInfo
        };
        timeout.Token.ThrowIfCancellationRequested();
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The exact Development Git evidence command could not be started.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("The exact Development Git evidence command could not be started.", exception);
        }

        try
        {
            var outputTask = ReadCappedAndDrainAsync(process.StandardOutput.BaseStream, maxOutputBytes, timeout.Token);
            var errorTask = ReadCappedAndDrainAsync(process.StandardError.BaseStream, 64 * 1024, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (output.Truncated || error.Truncated)
            {
                throw new InvalidDataException("The exact Development Git evidence output exceeded its configured bound.");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("The exact Development patch evidence could not be exported: "
                                                    + Encoding.UTF8.GetString(error.Bytes));
            }

            return new ExactGitResult(output.Bytes);
        }
        catch
        {
            ProcessTermination.TryKill(process);
            throw;
        }
    }

    private static IReadOnlyList<DevelopmentChangedFile> ParseStatus(byte[] status)
    {
        var tokens = SplitNullTerminated(status);
        var result = new List<DevelopmentChangedFile>();
        for (var index = 0; index < tokens.Length;)
        {
            var code = tokens[index++];
            if (index >= tokens.Length)
            {
                throw new InvalidDataException("The Git changed-file manifest was truncated.");
            }

            var path = tokens[index++];
            string? previousPath = null;
            if ((code.StartsWith('R') || code.StartsWith('C')) && index < tokens.Length)
            {
                previousPath = path;
                path = tokens[index++];
            }

            result.Add(new DevelopmentChangedFile(path, ChangeType(code), previousPath));
        }

        return result.OrderBy(static item => item.Path, StringComparer.Ordinal).ToArray();
    }

    private static string ChangeType(string status) =>
        status.Length == 0
            ? "unknown"
            : status[0] switch
            {
                'A' => "added",
                'M' => "modified",
                'D' => "deleted",
                'R' => "renamed",
                'C' => "copied",
                'T' => "typechanged",
                _ => "unknown"
            };

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content));

    private static string[] SplitNullTerminated(byte[] content)
    {
        if (content.Length == 0 || content[^1] != 0)
        {
            throw new InvalidDataException("The Git changed-file manifest was truncated.");
        }

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var tokens = new List<string>();
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] != 0)
            {
                continue;
            }

            if (index > start)
            {
                tokens.Add(utf8.GetString(content.AsSpan(start, index - start)));
            }

            start = index + 1;
        }

        return tokens.ToArray();
    }

    private static async Task<CappedBytes> ReadCappedAndDrainAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        using var captured = new MemoryStream(Math.Min(maxBytes, buffer.Length));
        var truncated = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = maxBytes - (int)captured.Length;
            if (remaining > 0)
            {
                await captured.WriteAsync(buffer.AsMemory(0, Math.Min(read, remaining)), cancellationToken).ConfigureAwait(false);
            }

            truncated |= read > remaining;
        }

        return new CappedBytes(captured.ToArray(), truncated);
    }

    private static void CopyEnvironment(ProcessStartInfo startInfo, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is not null)
        {
            startInfo.Environment[name] = value;
        }
    }

    private sealed record ExactGitResult(byte[] StandardOutput);

    private sealed record CappedBytes(byte[] Bytes, bool Truncated);
}
