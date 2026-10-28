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
/// </summary>
/// <remarks>
///     No sandbox provider, deliberately: every Git command runs on the host (see <see cref="RunGitExactAsync" />),
///     because the subject hash the operator approves must come from the engine's own Git. Nor is the worktree's own
///     <c>.git</c> trusted, every sandboxed command being able to write it: each call stages into an engine-owned
///     repository (see <see cref="StageAsync" />), so refs, replace refs, index bits and ignore files there change nothing.
/// </remarks>
internal sealed class DevelopmentPatchEvidenceService : IDevelopmentPatchEvidenceService
{
    private const string StagePrefix = "stage-";

    /// <summary>More Git commands than any export runs, each bounded by the attempt cap; a stage older than that many caps is no live export's.</summary>
    private const int MaxCommandsPerStage = 8;

    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

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

        using var stage = await StageAsync(session, cancellationToken);
        var patch = await RunGitExactAsync(session,
            stage,
            ["diff", "--cached", "--binary", "--full-index", "--no-ext-diff", "--ignore-submodules=none", session.BaseCommit, "--", "."],
            maxOutputBytes: _options.MaxPatchBytes,
            cancellationToken);
        var status = await RunGitExactAsync(session,
            stage,
            ["diff", "--cached", "--name-status", "-z", "--ignore-submodules=none", session.BaseCommit, "--", "."],
            maxOutputBytes: _options.MaxPatchBytes,
            cancellationToken);
        var raw = await RunGitExactAsync(session,
            stage,
            ["diff", "--cached", "--raw", "-z", "--ignore-submodules=none", session.BaseCommit, "--", "."],
            maxOutputBytes: _options.MaxPatchBytes,
            cancellationToken);
        var resultTree = await RunGitExactAsync(session,
            stage,
            ["write-tree"],
            maxOutputBytes: 4096,
            cancellationToken);

        var patchBytes = patch.StandardOutput;
        if (patchBytes.Length == 0 || patchBytes.Length > _options.MaxPatchBytes)
        {
            throw new InvalidOperationException("The final Development patch is empty or exceeds the configured patch limit.");
        }

        // A nested .git still makes add -A stage its directory as one gitlink, hiding every file inside it. The index is
        // the stage's own and --ignore-submodules=none outranks any .gitmodules, so nothing can hide that entry here.
        var stagedTree = Encoding.ASCII.GetString(resultTree.StandardOutput).Trim();
        if (raw.StandardOutput.Length > 0 && TouchesGitlink(raw.StandardOutput))
        {
            throw new DevelopmentWorkspaceSecurityException("The final patch adds, changes or removes a nested Git repository (gitlink).");
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
        return new DevelopmentPatchEvidence
        {
            BaseCommit = session.BaseCommit,
            PatchHash = patchHash,
            ManifestHash = manifestHash,
            SubjectHash = subjectHash,
            ExpectedResultHash = stagedTree,
            PatchBytes = patchBytes,
            ManifestBytes = manifestBytes,
            ChangedFiles = changedFiles
        };
    }

    /// <summary>The workspace paths that differ from the base commit right now.</summary>
    /// <remarks>
    ///     It shares its staging and its <c>--name-status</c> parse with <see cref="ExportAsync" />, so the two can
    ///     never disagree about how a path is spelled. Unlike the export it tolerates an empty result, the ordinary
    ///     state of a task's first attempt; its only size bound is <c>MaxPatchBytes</c> on the <c>--name-status</c>
    ///     output, which <see cref="RunGitExactAsync" /> enforces.
    /// </remarks>
    public async Task<IReadOnlySet<string>> ListChangedPathsAsync(DevelopmentWorkspaceSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        using var stage = await StageAsync(session, cancellationToken);
        var status = await RunGitExactAsync(session,
            stage,
            ["diff", "--cached", "--name-status", "-z", "--ignore-submodules=none", session.BaseCommit, "--", "."],
            maxOutputBytes: _options.MaxPatchBytes,
            cancellationToken);
        return status.StandardOutput.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)

            // Both ends of a rename: renaming b back to a has changed a, and a caller comparing a submission against
            // this set would else refuse the shape it exists to forgive. ExportAsync judges escaping paths at the end.
            : ParseStatus(status.StandardOutput)
              .SelectMany(static item => item.PreviousPath is null ? (string[])[item.Path] : [item.Path, item.PreviousPath])
              .Where(static path => DevelopmentWorkspaceSecurity.Confine(path, allowRoot: false).IsAccepted)
              .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    ///     A fresh bare repository under the never-mounted runtime directory, its index read from the base commit and
    ///     then refreshed from the worktree's files by <c>add -A</c>.
    /// </summary>
    /// <remarks>
    ///     Objects from the operator's checkout through <c>objects/info/alternates</c>, in its object format; the base
    ///     commit from the engine-owned manifest; no refs. Git reads the worktree's files byte for byte and never stages
    ///     a <c>.git</c> component; a nested repository still becomes a gitlink, which the export refuses. A stage a hard
    ///     stop left goes once older than <see cref="MaxCommandsPerStage" /> commands may run, judged on the file system's
    ///     own clock against the new stage's.
    /// </remarks>
    private async Task<EvidenceStage> StageAsync(DevelopmentWorkspaceSession session, CancellationToken cancellationToken)
    {
        // First because the provider runs host Git inside the worktree too.
        await DevelopmentWorkspaceGitConfig.RestoreMinimalAsync(session.HostWorktreePath, cancellationToken);
        var stage = new EvidenceStage(Path.Combine(session.RuntimePath, StagePrefix + Guid.NewGuid().ToString("N")));
        try
        {
            var staleBefore = stage.CreatedAtUtc.AddSeconds(-(double)_options.MaxAttemptDurationSeconds * MaxCommandsPerStage);
            foreach (var leftover in new DirectoryInfo(session.RuntimePath).EnumerateDirectories(StagePrefix + "*"))
            {
                if (leftover.LastWriteTimeUtc < staleBefore)
                {
                    StandaloneGitClone.TryDelete(leftover.FullName);
                }
            }

            _ = await RunGitAsync(session,
                stage.Home,
                AgentHomeGit.Arguments("init", "--quiet", "--bare", "--object-format=" + session.ObjectFormat, stage.GitDirectory),
                maxOutputBytes: 4096,
                cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(stage.GitDirectory, "objects", "info", "alternates"),
                Path.Combine(session.TrustedGitDirectory, "objects") + "\n",
                cancellationToken);
            _ = await RunGitExactAsync(session, stage, ["read-tree", session.BaseCommit], maxOutputBytes: 4096, cancellationToken);
            _ = await RunGitExactAsync(session, stage, ["add", "-A", "--", "."], maxOutputBytes: 4096, cancellationToken);
            return stage;
        }
        catch
        {
            stage.Dispose();
            throw;
        }
    }

    /// <summary>Git scoped to <paramref name="stage" />'s repository, over the task's worktree.</summary>
    /// <remarks>
    ///     The HOME is the stage's own empty directory and <c>core.excludesFile</c> is empty, because its default is a file
    ///     under the config home and the coder's sandbox writes the task's HOME, where a pattern can hide an untracked file.
    /// </remarks>
    private Task<ExactGitResult> RunGitExactAsync(DevelopmentWorkspaceSession session,
        EvidenceStage stage,
        IReadOnlyList<string> tail,
        int maxOutputBytes,
        CancellationToken cancellationToken) =>
        RunGitAsync(session,
            stage.Home,
            AgentHomeGit.Arguments(["-c", "core.excludesFile=", $"--git-dir={stage.GitDirectory}", $"--work-tree={session.HostWorktreePath}", .. tail]),
            maxOutputBytes,
            cancellationToken);

    private async Task<ExactGitResult> RunGitAsync(DevelopmentWorkspaceSession session,
        string home,
        IReadOnlyList<string> arguments,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        CopyEnvironment(startInfo, "PATH");
        CopyEnvironment(startInfo, "SystemRoot");
        CopyEnvironment(startInfo, "windir");
        startInfo.Environment["HOME"] = home;
        startInfo.Environment["XDG_CONFIG_HOME"] = home;
        startInfo.Environment["TMPDIR"] = home;
        startInfo.Environment["TMP"] = home;
        startInfo.Environment["TEMP"] = home;
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        // HOME alone left $HOME/.gitconfig and the system file readable; the node-wide hardening removes both from git's search.
        foreach (var (name, value) in AgentHomeGitHardening.Environment)
        {
            startInfo.Environment[name] = value;
        }

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
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (output.Truncated || error.Truncated)
            {
                throw new InvalidDataException("The exact Development Git evidence output exceeded its configured bound.");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("The exact Development patch evidence could not be exported: "
                                                    + Encoding.UTF8.GetString(error.Bytes));
            }

            return new ExactGitResult
            {
                StandardOutput = output.Bytes
            };
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

            result.Add(new DevelopmentChangedFile
            {
                Path = path,
                ChangeType = ChangeType(code),
                PreviousPath = previousPath
            });
        }

        return result.OrderBy(static item => item.Path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Whether a <c>--raw -z</c> record has a gitlink (mode <c>160000</c>) on either side.</summary>
    /// <remarks>Read as Latin-1, which maps every byte, so a path that is not UTF-8 never fails the check.</remarks>
    private static bool TouchesGitlink(byte[] raw)
    {
        var tokens = Encoding.Latin1.GetString(raw).Split('\0');
        for (var index = 0; index < tokens.Length && tokens[index].Length > 0;)
        {
            // ":<old mode> <new mode> <old id> <new id> <status>", then one path, or two for a rename or copy.
            var fields = tokens[index].Split(' ');
            if (fields.Length != 5)
            {
                throw new InvalidDataException("The Git raw changed-file manifest was malformed.");
            }

            if (string.Equals(fields[0], ":160000", StringComparison.Ordinal) || string.Equals(fields[1], "160000", StringComparison.Ordinal))
            {
                return true;
            }

            index += fields[4].StartsWith('R') || fields[4].StartsWith('C') ? 3 : 2;
        }

        return false;
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
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            var remaining = maxBytes - (int)captured.Length;
            if (remaining > 0)
            {
                await captured.WriteAsync(buffer.AsMemory(0, Math.Min(read, remaining)), cancellationToken);
            }

            truncated |= read > remaining;
        }

        return new CappedBytes
        {
            Bytes = captured.ToArray(),
            Truncated = truncated
        };
    }

    private static void CopyEnvironment(ProcessStartInfo startInfo, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is not null)
        {
            startInfo.Environment[name] = value;
        }
    }

    /// <summary>The engine-owned staging repository of one export and its empty HOME, deleted with its scope.</summary>
    private sealed class EvidenceStage : IDisposable
    {
        private readonly string _root;

        public EvidenceStage(string root)
        {
            _root = root;
            Directory.CreateDirectory(Home);
        }

        public string GitDirectory => Path.Combine(_root, "git");

        public string Home => Path.Combine(_root, "home");

        public DateTime CreatedAtUtc => Directory.GetLastWriteTimeUtc(_root);

        public void Dispose() => StandaloneGitClone.TryDelete(_root);
    }

    private sealed record ExactGitResult
    {
        public required byte[] StandardOutput { get; init; }
    }

    private sealed record CappedBytes
    {
        public required byte[] Bytes { get; init; }

        public required bool Truncated { get; init; }
    }
}
