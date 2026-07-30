namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;

public static class DevelopmentCommandIds
{
    public const string GitStatus = "git_status";
    public const string GitDiffCheck = "git_diff_check";
    public const string DotnetRestore = "dotnet_restore";
    public const string DotnetBuildRelease = "dotnet_build_release_no_restore";
    public const string DotnetTestRelease = "dotnet_test_release_no_build";
}

internal sealed record DevelopmentCommandEvidence(
    string CommandId,
    int ExitCode,
    bool Completed,
    bool OutputTruncated,
    long DurationMilliseconds,
    string StandardOutput,
    string StandardError,

    /// <summary>
    ///     The structured test result for this command, or null when the command produces none. Read by a code-owned
    ///     <see cref="IDevelopmentTestResultAdapter" /> from the command's raw output before that output is truncated
    ///     for evidence — see <see cref="DevelopmentWorkspaceTools.ExecuteCatalogAsync" />.
    /// </summary>
    DevelopmentTestOutcome? TestOutcome = null);

internal interface IDevelopmentWorkspaceTools
{
    IReadOnlyList<DevelopmentCommandEvidence> CommandEvidence { get; }
    DevelopmentCommandProfile Profile { get; }
    Task<string> ListFilesAsync(string? path, CancellationToken cancellationToken = default);
    Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default);
    Task<string> SearchTextAsync(string pattern, string? path, CancellationToken cancellationToken = default);
    Task<string> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default);
    Task<string> ApplyPatchAsync(string patch, CancellationToken cancellationToken = default);
    Task<string> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<string> GetDiffAsync(CancellationToken cancellationToken = default);
    Task<string> RunCommandAsync(string commandId, CancellationToken cancellationToken = default);
}

internal sealed class DevelopmentWorkspaceTools : IDevelopmentWorkspaceTools
{
    /// <summary>
    ///     The artifact <em>protocol</em> version for coder-produced evidence. This is not the command-profile version:
    ///     it describes the shape of the artifacts this class's caller writes, and the apply and reviewer gates compare
    ///     their own protocol constants against it. Keep it, and keep it separate from
    ///     <see cref="DevelopmentCommandProfile.ComputeDigest" />.
    /// </summary>
    private const string CommandProfileVersion = "development-workspace-v1";

    private readonly List<DevelopmentCommandEvidence> _commandEvidence = [];

    /// <summary>
    ///     The import file's digest as the worktree presented it before this attempt ran anything, or null if absent.
    ///     <para>
    ///         Captured here rather than taken from <see cref="DevelopmentCommandProfile.ImportDigest" /> on purpose.
    ///         That digest was recorded at project creation from the operator's live repository working tree, whereas
    ///         the managed worktree is checked out at the attempt's base commit — so for a repository carrying an
    ///         uncommitted edit to <c>.xe-dev/profile.json</c> the two legitimately differ, and comparing against the
    ///         stored value would fail every attempt on its very first command. What the invariant needs to prove is
    ///         narrower and is exactly what this captures: that nothing THIS attempt ran changed the file.
    ///     </para>
    ///     <para>
    ///         Taken in the constructor, which runs immediately after the workspace is prepared and validated and
    ///         before any command executes, so no agent-influenced code has run yet.
    ///     </para>
    /// </summary>
    private readonly string? _importBaselineDigest;

    private readonly DevelopmentAttemptLiveProgress? _liveProgress;
    private readonly DevelopmentOptions _options;
    private readonly DevelopmentCommandProfile _profile;
    private readonly IDevelopmentSandboxRuntimeProvider _sandbox;
    private readonly DevelopmentWorkspaceSession _session;

    public DevelopmentWorkspaceTools(IDevelopmentSandboxRuntimeProvider sandbox,
        DevelopmentWorkspaceSession session,
        IOptions<DevelopmentOptions> options,
        DevelopmentCommandProfile profile,
        DevelopmentAttemptLiveProgress? liveProgress = null)
    {
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _liveProgress = liveProgress;
        _importBaselineDigest = DevelopmentCommandProfileImport.TryComputeDigest(session.HostWorktreePath);
        EnsureRuntimeDirectories();
    }

    public IReadOnlyList<DevelopmentCommandEvidence> CommandEvidence => _commandEvidence;

    public DevelopmentCommandProfile Profile => _profile;

    public async Task<string> ListFilesAsync(string? path, CancellationToken cancellationToken = default)
    {
        var confined = RequirePath(path, allowRoot: true);
        var result = await ExecuteAsync("tool_list_files",
            "find",
            ["-P", ".", "-maxdepth", "64", "-type", "f", "-print"],
            confined.SandboxPath,
            standardInput: null,
            cancellationToken).ConfigureAwait(false);
        EnsureCompleted(result, "list_files");
        return string.Join('\n', result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                       .Take(_options.MaxChangedFiles));
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var confined = RequirePath(path, allowRoot: false);
        return await _sandbox.ReadFileAsync(_session.SandboxHandle,
            confined.SandboxPath,
            _options.MaxCommandOutputBytes,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> SearchTextAsync(string pattern, string? path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var confined = RequirePath(path, allowRoot: true);
        var result = await ExecuteAsync("tool_search_text",
            "grep",
            ["-rnI", "-F", "-e", pattern, "--", "."],
            confined.SandboxPath,
            standardInput: null,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1 && string.IsNullOrEmpty(result.StandardError))
        {
            return string.Empty;
        }

        EnsureCompleted(result, "search_text");
        return result.StandardOutput;
    }

    public async Task<string> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var confined = RequirePath(path, allowRoot: false);
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > _options.MaxFileWriteBytes)
        {
            throw new InvalidOperationException("The requested file write exceeds the configured Development file limit.");
        }

        var tempPath = Path.Combine(_session.RuntimePath, "tmp", Guid.NewGuid().ToString("N") + ".write");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            await _sandbox.CopyIntoAsync(_session.SandboxHandle,
                new SandboxCopyRequest
                {
                    SourcePath = tempPath,
                    DestinationPath = confined.SandboxPath
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(tempPath);
        }

        _liveProgress?.FileChanged(confined.RelativePath, bytes.LongLength);
        return $"wrote {bytes.Length} byte(s) to {confined.RelativePath}";
    }

    public async Task<string> ApplyPatchAsync(string patch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var patchBytes = Encoding.UTF8.GetByteCount(patch);
        if (patchBytes > _options.MaxPatchBytes)
        {
            throw new InvalidOperationException("The requested patch exceeds the configured Development patch limit.");
        }

        if (patchBytes > _options.MaxFileWriteBytes)
        {
            throw new InvalidOperationException("The requested patch exceeds the configured Development file-write limit.");
        }

        var paths = ParsePatchPaths(patch);
        if (paths.Count == 0 || paths.Count > _options.MaxChangedFiles)
        {
            throw new DevelopmentWorkspaceSecurityException("The patch has no bounded, safely parseable Git paths.");
        }

        foreach (var path in paths)
        {
            var confined = RequirePath(path, allowRoot: false);
            try
            {
                _ = await _sandbox.ReadFileAsync(_session.SandboxHandle, confined.SandboxPath, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // A new file is allowed; ReadFileAsync already verified every existing parent component without following symlinks.
            }
        }

        var check = await ExecuteAsync("tool_apply_patch_check",
            AgentHomeGit.Executable,
            AgentHomeGit.Arguments("apply", "--check", "--whitespace=error-all", "-"),
            "/",
            patch,
            cancellationToken).ConfigureAwait(false);
        EnsureCompleted(check, "apply_patch check");
        var apply = await ExecuteAsync("tool_apply_patch",
            AgentHomeGit.Executable,
            AgentHomeGit.Arguments("apply", "--whitespace=error-all", "-"),
            "/",
            patch,
            cancellationToken).ConfigureAwait(false);
        EnsureCompleted(apply, "apply_patch");
        foreach (var path in paths)
        {
            _liveProgress?.FileChanged(path, patchBytes);
        }

        return $"applied patch for {paths.Count} path(s)";
    }

    public async Task<string> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCatalogAsync(DevelopmentCommandIds.GitStatus, cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    public async Task<string> GetDiffAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync("tool_git_diff",
            AgentHomeGit.Executable,
            AgentHomeGit.Arguments("diff", "--binary", "--", "."),
            "/",
            standardInput: null,
            cancellationToken).ConfigureAwait(false);
        EnsureCompleted(result, "git diff");
        return result.StandardOutput;
    }

    public async Task<string> RunCommandAsync(string commandId, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCatalogAsync(commandId, cancellationToken).ConfigureAwait(false);
        return $"{commandId}: exit={result.ExitCode}, completed={result.Completed}\n{result.StandardOutput}\n{result.StandardError}";
    }

    internal const string ProfileVersion = CommandProfileVersion;

    private async Task<SandboxCommandResult> ExecuteCatalogAsync(string commandId, CancellationToken cancellationToken)
    {
        var command = _profile.ResolveCommand(commandId);

        _liveProgress?.CommandStarted(commandId);

        // Raw first, truncated second, and the result adapter reads the raw copy. A runner prints its result summary
        // LAST and Truncate keeps the HEAD, so parsing the evidence copy would lose the summary on any repository
        // verbose enough to exceed MaxCommandOutputBytes — turning a perfectly readable green or red into an
        // unparseable one, which fails validation. Only bytes the sandbox itself dropped are genuinely unrecoverable.
        var raw = await ExecuteRawAsync(commandId,
            command.Executable,
            command.Arguments,
            "/",
            standardInput: null,
            ResolveTimeout(command.TimeoutSeconds),
            cancellationToken).ConfigureAwait(false);
        var testOutcome = DevelopmentTestResultAdapters.Resolve(_profile, commandId)
                                                       ?.Parse(raw.StandardOutput,
                                                           raw.StandardError,
                                                           raw.StandardOutputTruncated || raw.StandardErrorTruncated);

        var result = TruncateForEvidence(raw);
        await EnsureWorkspaceInvariantAsync(cancellationToken).ConfigureAwait(false);
        var evidence = new DevelopmentCommandEvidence(commandId,
            result.ExitCode,
            result.Completed,
            result.StandardOutputTruncated || result.StandardErrorTruncated,
            (long)result.Duration.TotalMilliseconds,
            result.StandardOutput,
            result.StandardError,
            testOutcome);
        _commandEvidence.Add(evidence);
        _liveProgress?.CommandCompleted(evidence);
        return result;
    }

    private async Task EnsureWorkspaceInvariantAsync(CancellationToken cancellationToken)
    {
        var head = await ExecuteAsync("verify_detached_head",
            AgentHomeGit.Executable,
            AgentHomeGit.Arguments("rev-parse", "--verify", "HEAD^{commit}"),
            "/",
            standardInput: null,
            cancellationToken).ConfigureAwait(false);
        EnsureCompleted(head, "verify Development worktree HEAD");
        if (!string.Equals(head.StandardOutput.Trim(), _session.BaseCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new DevelopmentWorkspaceSecurityException("A fixed command changed the managed Development worktree base commit.");
        }

        var branch = await ExecuteAsync("verify_detached_branch",
            AgentHomeGit.Executable,
            AgentHomeGit.Arguments("symbolic-ref", "--quiet", "HEAD"),
            "/",
            standardInput: null,
            cancellationToken).ConfigureAwait(false);
        if (!branch.Completed || branch.ExitCode is not (0 or 1))
        {
            throw new InvalidOperationException("The managed Development worktree branch state could not be verified.");
        }

        if (branch.ExitCode == 0)
        {
            throw new DevelopmentWorkspaceSecurityException("A fixed command attached the managed Development worktree to a protected branch.");
        }

        EnsureCommandProfileImportUnchanged();
    }

    /// <summary>
    ///     Re-checks the repository's <c>.xe-dev/profile.json</c> against the digest recorded when the profile was
    ///     imported, after every catalog command.
    ///     <para>
    ///         Adding <c>.xe-dev</c> to <see cref="DevelopmentWorkspaceSecurity" />'s deny list only stops the agent
    ///         naming that path as an argument to a workspace tool. It does nothing about a build or test command that
    ///         writes the file as a side effect — an MSBuild target, a post-install script, or simply a test that writes
    ///         where it should not. This check is what closes that, and it is why the deny-list entry must not be
    ///         mistaken for the guard.
    ///     </para>
    ///     <para>
    ///         Read from the host worktree rather than through the sandbox: the engine is verifying its own invariant,
    ///         so routing the read through the surface being verified would be circular.
    ///     </para>
    /// </summary>
    private void EnsureCommandProfileImportUnchanged()
    {
        var actual = DevelopmentCommandProfileImport.TryComputeDigest(_session.HostWorktreePath);
        if (!string.Equals(actual, _importBaselineDigest, StringComparison.Ordinal))
        {
            throw new DevelopmentWorkspaceSecurityException(
                "A fixed command changed the repository command-profile import file in the managed Development worktree.");
        }
    }

    /// <summary>
    ///     Runs one of the engine's own fixed helper commands (directory listing, text search, patch application, diff,
    ///     and the post-command workspace invariant probes). These are bounded by
    ///     <see cref="DevelopmentOptions.ToolCommandTimeoutSeconds" /> rather than by the attempt cap: before per-command
    ///     timeouts existed, every one of them could individually consume the whole 30-minute attempt budget, so a hung
    ///     <c>grep</c> was indistinguishable from a legitimately long build.
    /// </summary>
    private Task<SandboxCommandResult> ExecuteAsync(string executionPrefix,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? standardInput,
        CancellationToken cancellationToken) =>
        ExecuteAsync(executionPrefix,
            executable,
            arguments,
            workingDirectory,
            standardInput,
            ResolveTimeout(_options.ToolCommandTimeoutSeconds),
            cancellationToken);

    private async Task<SandboxCommandResult> ExecuteAsync(string executionPrefix,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        TruncateForEvidence(await ExecuteRawAsync(executionPrefix,
            executable,
            arguments,
            workingDirectory,
            standardInput,
            timeout,
            cancellationToken).ConfigureAwait(false));

    /// <summary>
    ///     Runs a command and returns its output as the sandbox produced it, capped only by the sandbox's own stream
    ///     limit. Callers that persist the result must pass it through <see cref="TruncateForEvidence" /> first; the
    ///     only reason to hold the untruncated form is to read structure out of it, because the engine's evidence cap
    ///     keeps the head and the structure a test runner emits is at the tail.
    /// </summary>
    private Task<SandboxCommandResult> ExecuteRawAsync(string executionPrefix,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        _sandbox.ExecuteAsync(_session.SandboxHandle, new SandboxCommandRequest
        {
            ExecutionId = executionPrefix + "-" + Guid.NewGuid().ToString("N"),
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            StandardInput = standardInput,
            Environment = BuildEnvironment(),
            Timeout = timeout
        }, cancellationToken);

    private SandboxCommandResult TruncateForEvidence(SandboxCommandResult result)
    {
        var outputTruncated = Encoding.UTF8.GetByteCount(result.StandardOutput) > _options.MaxCommandOutputBytes;
        var errorTruncated = Encoding.UTF8.GetByteCount(result.StandardError) > _options.MaxCommandOutputBytes;
        return result with
        {
            StandardOutput = Truncate(result.StandardOutput, _options.MaxCommandOutputBytes),
            StandardError = Truncate(result.StandardError, _options.MaxCommandOutputBytes),
            StandardOutputTruncated = result.StandardOutputTruncated || outputTruncated,
            StandardErrorTruncated = result.StandardErrorTruncated || errorTruncated
        };
    }

    /// <summary>
    ///     Clamps a per-command budget to the attempt cap. A profile can ask for less than the attempt allows but never
    ///     for more, so <see cref="DevelopmentOptions.MaxAttemptDurationSeconds" /> stays the outer bound it claims to be.
    /// </summary>
    private TimeSpan ResolveTimeout(int requestedSeconds) =>
        TimeSpan.FromSeconds(Math.Min(Math.Max(requestedSeconds, 1), _options.MaxAttemptDurationSeconds));

    private IReadOnlyDictionary<string, string> BuildEnvironment()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = Path.Combine(_session.RuntimePath, "home"),
            ["TMPDIR"] = Path.Combine(_session.RuntimePath, "tmp"),
            ["TMP"] = Path.Combine(_session.RuntimePath, "tmp"),
            ["TEMP"] = Path.Combine(_session.RuntimePath, "tmp"),
            ["NUGET_PACKAGES"] = Path.Combine(_session.RuntimePath, "nuget"),
            ["DOTNET_CLI_HOME"] = Path.Combine(_session.RuntimePath, "dotnet"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1"
        };
    }

    private void EnsureRuntimeDirectories()
    {
        foreach (var name in new[]
                 {
                     "home",
                     "tmp",
                     "nuget",
                     "dotnet"
                 })
        {
            Directory.CreateDirectory(Path.Combine(_session.RuntimePath, name));
        }
    }

    private static DevelopmentConfinedPath RequirePath(string? path, bool allowRoot)
    {
        var confined = DevelopmentWorkspaceSecurity.Confine(path, allowRoot);
        return confined.IsAccepted
            ? confined
            : throw new DevelopmentWorkspaceSecurityException(confined.RejectionReason ?? "The workspace path was rejected.");
    }

    private static HashSet<string> ParsePatchPaths(string patch)
    {
        if (patch.Contains("120000", StringComparison.Ordinal))
        {
            throw new DevelopmentWorkspaceSecurityException("Patches cannot create or modify symbolic links.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var insideHunk = false;
        foreach (var rawLine in patch.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                insideHunk = false;
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 4 || !fields[2].StartsWith("a/", StringComparison.Ordinal) || !fields[3].StartsWith("b/", StringComparison.Ordinal))
                {
                    throw new DevelopmentWorkspaceSecurityException("Quoted or ambiguous patch paths are not accepted by the bounded patch tool.");
                }

                AddPatchPath(paths, fields[2][2..]);
                AddPatchPath(paths, fields[3][2..]);
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                insideHunk = true;
                continue;
            }

            if (insideHunk)
            {
                continue;
            }

            if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                AddPatchPath(paths, line["rename from ".Length..]);
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                AddPatchPath(paths, line["rename to ".Length..]);
            }
            else if (line.StartsWith("copy from ", StringComparison.Ordinal))
            {
                AddPatchPath(paths, line["copy from ".Length..]);
            }
            else if (line.StartsWith("copy to ", StringComparison.Ordinal))
            {
                AddPatchPath(paths, line["copy to ".Length..]);
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                AddFileHeaderPath(paths, line["--- ".Length..], "a/");
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                AddFileHeaderPath(paths, line["+++ ".Length..], "b/");
            }
        }

        return paths;
    }

    private static void AddFileHeaderPath(HashSet<string> paths, string value, string prefix)
    {
        if (string.Equals(value, "/dev/null", StringComparison.Ordinal))
        {
            return;
        }

        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new DevelopmentWorkspaceSecurityException("Quoted or ambiguous patch file headers are not accepted by the bounded patch tool.");
        }

        AddPatchPath(paths, value[prefix.Length..]);
    }

    private static void AddPatchPath(HashSet<string> paths, string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path[0] == '"'
            || path.Any(char.IsControl))
        {
            throw new DevelopmentWorkspaceSecurityException("Quoted or ambiguous patch paths are not accepted by the bounded patch tool.");
        }

        paths.Add(path);
    }

    private static void EnsureCompleted(SandboxCommandResult result, string operation)
    {
        if (!result.Completed || result.ExitCode != 0)
        {
            throw new InvalidOperationException($"The fixed Development {operation} operation failed.");
        }
    }

    private static string Truncate(string value, int byteLimit)
    {
        if (Encoding.UTF8.GetByteCount(value) <= byteLimit)
        {
            return value;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        return Encoding.UTF8.GetString(bytes.AsSpan(0, byteLimit));
    }
}
