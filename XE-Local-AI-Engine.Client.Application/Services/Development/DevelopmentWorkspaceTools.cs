namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

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

    /// <summary>
    ///     The product's single definition of "this file may hold a credential". Read paths here call its
    ///     <see cref="ISensitiveFileExclusionService.IsSecret" /> predicate, deliberately NOT the broader
    ///     <see cref="ISensitiveFileExclusionService.IsExcluded" /> copy filter — that one also names build output,
    ///     which an agent legitimately reads after a failed build and which is not a credential.
    ///     <para>
    ///         This is a MITIGATION, not a boundary. It removes the one-step path — the coder or reviewer model naming
    ///         a secret directly to <c>read_file</c>/<c>search_text</c>, on its own initiative or steered by
    ///         prompt-injected content in the repository it was asked to read — and nothing more. Development Mode also
    ///         EXECUTES the repository's own build and test commands, and a test that prints <c>.env</c> puts those
    ///         bytes into captured stdout, which reaches the same attempt context and the same cloud role route. A
    ///         hostile repository's secrets are not made safe by this check; only the trivial path to them is closed.
    ///     </para>
    /// </summary>
    private static readonly ISensitiveFileExclusionService DefaultExclusions = new SensitiveFileExclusionService();

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

    private readonly ISensitiveFileExclusionService _exclusions;
    private readonly DevelopmentAttemptLiveProgress? _liveProgress;
    private readonly DevelopmentOptions _options;
    private readonly DevelopmentCommandProfile _profile;
    private readonly IDevelopmentSandboxRuntimeProvider _sandbox;
    private readonly DevelopmentWorkspaceSession _session;

    public DevelopmentWorkspaceTools(IDevelopmentSandboxRuntimeProvider sandbox,
        DevelopmentWorkspaceSession session,
        IOptions<DevelopmentOptions> options,
        DevelopmentCommandProfile profile,
        DevelopmentAttemptLiveProgress? liveProgress = null,
        ISensitiveFileExclusionService? exclusions = null)
    {
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _liveProgress = liveProgress;
        _exclusions = exclusions ?? DefaultExclusions;
        _importBaselineDigest = DevelopmentCommandProfileImport.TryComputeDigest(session.HostWorktreePath);
        EnsureRuntimeDirectories();
    }

    public IReadOnlyList<DevelopmentCommandEvidence> CommandEvidence => _commandEvidence;

    public DevelopmentCommandProfile Profile => _profile;

    /// <summary>
    ///     Lists the workspace's regular files. Served by <see cref="WorkspaceFileScanner" /> rather than by
    ///     <c>find</c> — see that class for why the engine owns this operation on every platform rather than branching
    ///     on Windows.
    ///     <para>
    ///         The suppression predicate is applied at the PRUNE step, not only to the finished list. Pruning is what
    ///         makes the listing usable at all: the output budget is spent before any post-filter runs, so on a
    ///         workspace whose Git metadata alone outruns it every surviving entry named a suppressed path, the filter
    ///         discarded all of them, and <c>list_files</c> answered with nothing while the workspace was full of files
    ///         the agent could act on.
    ///     </para>
    /// </summary>
    public Task<string> ListFilesAsync(string? path, CancellationToken cancellationToken = default)
    {
        var confined = RequirePath(path, allowRoot: true);
        var entries = RunScan(confined,
            "list_files",
            (root, token) => WorkspaceFileScanner.ListFiles(root,
                _options.MaxChangedFiles,
                IsSuppressedFromOutput,
                nameGlob: null,
                token),
            cancellationToken);
        return Task.FromResult(string.Join('\n', entries));
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var confined = RequirePath(path, allowRoot: false);
        EnsureNotSecret(confined.RelativePath);
        return await _sandbox.ReadFileAsync(_session.SandboxHandle,
            confined.SandboxPath,
            _options.MaxCommandOutputBytes,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Searches the workspace for a LITERAL string. Served by <see cref="WorkspaceFileScanner" /> rather
    ///     than by <c>grep</c> — see that class for why.
    ///     <para>
    ///         Credential-bearing entries are excluded by PRUNING, so a secret's CONTENT never enters the result in the
    ///         first place; the emitted line is not filtered afterwards because it is never produced. Build output is
    ///         deliberately not excluded — searching <c>bin</c>/<c>obj</c>/<c>node_modules</c> is legitimate and leaks
    ///         nothing.
    ///     </para>
    ///     <para>
    ///         The pattern is matched ordinally as a fixed string, never compiled as a regular expression. The
    ///         shell-out passed <c>grep -F</c> with the pattern bound via <c>-e</c> for the same reason; managed code
    ///         gets the property for free, because a model-supplied value can no longer be read as a flag or as a
    ///         catastrophically backtracking expression at all.
    ///     </para>
    /// </summary>
    public Task<string> SearchTextAsync(string pattern, string? path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var confined = RequirePath(path, allowRoot: true);
        var matches = RunScan(confined,
            "search_text",
            (root, token) => WorkspaceFileScanner.SearchText(root,
                pattern,
                isRegex: false,
                // Bounded by bytes alone, exactly as the shell-out was: the evidence cap is the contract here, and a
                // separate match count would be a second limit nobody configured.
                maxMatches: int.MaxValue,
                _options.MaxCommandOutputBytes,
                IsSuppressedFromOutput,
                token),
            cancellationToken);
        return Task.FromResult(string.Join('\n', matches));
    }

    /// <summary>
    ///     Runs one of the two managed workspace surveys against the confined host directory, under the same per-tool
    ///     budget the shell-out ran under (<see cref="DevelopmentOptions.ToolCommandTimeoutSeconds" />, itself clamped
    ///     to the attempt cap) so a pathological tree cannot consume the whole attempt.
    ///     <para>
    ///         Reads the HOST worktree rather than routing through the sandbox, which is what the engine already does
    ///         for its own workspace invariants (<see cref="EnsureCommandProfileImportUnchanged" />) and for evidence
    ///         export. Every provider's workspace is that same directory — the process provider identity-maps it and
    ///         the container provider bind-mounts it — so the bytes surveyed are the bytes a command inside the sandbox
    ///         would see.
    ///     </para>
    ///     <para>
    ///         The failure sentence is kept identical to the one <see cref="EnsureCompleted" /> produced for these two
    ///         operations, because it is what the operator-facing task output and the Windows RC runbook both name.
    ///     </para>
    /// </summary>
    private List<string> RunScan(DevelopmentConfinedPath confined,
        string operation,
        Func<string, CancellationToken, List<string>> scan,
        CancellationToken cancellationToken)
    {
        var root = confined.RelativePath.Length == 0
            ? _session.HostWorktreePath
            : Path.Combine(_session.HostWorktreePath, confined.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ResolveTimeout(_options.ToolCommandTimeoutSeconds));
        try
        {
            return scan(root, timeoutCts.Token);
        }
        catch (WorkspaceScanRejectedException exception)
        {
            // Keep Development's own security-exception type at its boundary: the attempt lane distinguishes a security
            // refusal from an operational failure, and the scanner's neutral type would be read as the latter.
            throw new DevelopmentWorkspaceSecurityException(exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The fixed Development {operation} operation failed.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The fixed Development {operation} operation failed.");
        }
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
            throw new DevelopmentWorkspaceSecurityException("A fixed command changed the repository command-profile import file in the managed Development worktree.");
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

    /// <summary>
    ///     The environment every sandboxed command runs under, with every path expressed in the SANDBOX's namespace
    ///     rather than the host's.
    ///     <para>
    ///         This used to emit absolute host paths, which worked only because the child ran on the host. Inside a
    ///         container none of them exist and the root filesystem is read-only, so <c>dotnet restore</c>, <c>build</c>
    ///         and <c>test</c> all fail — and they fail obscurely, because the error names a directory the container has
    ///         never heard of. The mapping comes from the sandbox handle, so the process provider (which identity-maps
    ///         and therefore still emits host paths) is byte-identical to what it did before.
    ///     </para>
    /// </summary>
    private IReadOnlyDictionary<string, string> BuildEnvironment()
    {
        var home = ResolveRuntimeDirectory("home");
        var temporary = ResolveRuntimeDirectory("tmp");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["TMPDIR"] = temporary,
            ["TMP"] = temporary,
            ["TEMP"] = temporary,
            ["NUGET_PACKAGES"] = ResolveRuntimeDirectory("nuget"),
            // The per-task NUGET_PACKAGES above must not outlive the task, and with node reuse on it did. MSBuild's
            // reusable worker nodes (MSBuild.dll /nodemode:1) survive the dotnet process that started them, keeping
            // that per-task path in their environment; on the process provider they are host processes, so a LATER
            // restore anywhere on this box can attach to one and write the by-then-deleted packages path into
            // obj/*.dgspec.json. Measured twice: NU5037 during the graph-workflows merge and CS0006 in the session
            // after it, both naming a /tmp/xe-… directory no command had asked for. One task per node, no reuse.
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_HOME"] = ResolveRuntimeDirectory("dotnet"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            // Without this, the .NET CLI's first-run experience appends "$DOTNET_CLI_HOME/.dotnet/tools" to the
            // PERSISTED per-user PATH (on Windows, the HKCU\Environment registry value). DOTNET_CLI_HOME above is a
            // fresh per-task directory, so every task leaked one more entry that outlived the directory it named.
            // Measured on Windows 11 2026-08-03: 153 dead entries, 28 387 characters, and the entry count still
            // climbing during a single session. The damage is not untidiness — cmd.exe silently receives an EMPTY
            // %PATH% once the variable grows past its limit, so every bare-name command run through it fails. That
            // broke three sandbox tests whose fixture is "cmd /c ping -n 31": ping could not resolve, the command
            // exited instantly, and cancel/timeout/tree-kill had nothing left to kill. Stripping the dead entries
            // took PATH to 847 characters and the same tests went green with no code change.
            // DOTNET_SKIP_FIRST_TIME_EXPERIENCE is NOT an alternative — it is a no-op in .NET 10.
            ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "0"
        };
    }

    /// <summary>
    ///     Translates one per-task runtime directory into the path that names it inside the sandbox.
    ///     <para>
    ///         Throws rather than falling back to the host path when no mount covers it. A fallback would produce a
    ///         command that looks correct and fails deep inside a build against a directory that does not exist, which
    ///         is strictly harder to diagnose than a refusal here naming the missing mount.
    ///     </para>
    /// </summary>
    private string ResolveRuntimeDirectory(string name)
    {
        var hostPath = Path.Combine(_session.RuntimePath, name);
        return _session.SandboxHandle.TryResolveSandboxPath(hostPath)
               ?? throw new InvalidOperationException($"The sandbox does not expose the Development runtime directory '{name}'. The workspace provider must request it as an "
                                                      + "engine-generated mount before any command runs.");
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

    /// <summary>
    ///     Refuses a read whose path names a credential-bearing entry. See <see cref="DefaultExclusions" /> for what
    ///     this does and does not close: it removes the direct read, and it does nothing about a build or test command
    ///     that prints the same bytes to captured stdout.
    ///     <para>
    ///         Gates on <see cref="ISensitiveFileExclusionService.IsSecret" />, NOT on the broader copy filter. Build
    ///         output — <c>bin</c>, <c>obj</c>, <c>node_modules</c>, <c>dist</c> — stays readable, because reading it
    ///         after a failed build is a primary reason this feature exists and none of it is a credential.
    ///     </para>
    /// </summary>
    private void EnsureNotSecret(string relativePath)
    {
        if (IsSecretPath(relativePath))
        {
            // Names only the path the caller already supplied — no host path, no matched rule, no file content.
            throw new DevelopmentWorkspaceSecurityException($"'{relativePath}' is excluded because files with that name commonly hold credentials.");
        }
    }

    private bool IsSecretPath(string relativePath)
    {
        // Callers pass either a bare workspace-relative path or the "./a/b" shape the surveys emit, so the leading "."
        // and the empty segment before it are both skipped.
        return relativePath.Split('/')
                           .Where(static segment => !string.IsNullOrWhiteSpace(segment) && segment is not ("." or ".."))
                           .Any(segment => _exclusions.IsSecret(segment));
    }

    /// <summary>
    ///     What list_files and search_text neither descend into nor emit: credentials, plus the paths
    ///     <see cref="DevelopmentWorkspaceSecurity.Confine" /> already refuses as tool arguments. Build output is
    ///     deliberately absent from both — it is neither secret nor protected, and an agent reads it after a failed
    ///     build.
    ///     <para>
    ///         This one predicate is the whole rule now: <see cref="WorkspaceFileScanner" /> consults it to
    ///         prune a directory and again to admit a file, so there is no separately-built exclusion expression that
    ///         can drift away from it. Secrets match by NAME at any depth; protected trees match by their path rooted
    ///         at the SCANNED directory, which is why a listing taken from a subdirectory still suppresses secrets even
    ///         though the rooted prefixes match nothing there.
    ///     </para>
    /// </summary>
    private bool IsSuppressedFromOutput(string emittedPath)
    {
        // Accepts both shapes: the scanner passes "a/b" while an emitted survey line reads "./a/b". The
        // protected-prefix check needs the workspace-relative shape Confine sees.
        var relative = emittedPath.StartsWith("./", StringComparison.Ordinal) ? emittedPath[2..] : emittedPath;
        return IsSecretPath(relative) || DevelopmentWorkspaceSecurity.IsProtected(relative);
    }

    private static DevelopmentConfinedPath RequirePath(string? path, bool allowRoot)
    {
        var confined = DevelopmentWorkspaceSecurity.Confine(path, allowRoot);
        return confined.IsAccepted
            ? confined
            : throw new DevelopmentWorkspaceSecurityException(confined.RejectionReason ?? "The workspace path was rejected.");
    }

    private HashSet<string> ParsePatchPaths(string patch)
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
                AddSourcePatchPath(paths, line["rename from ".Length..]);
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                AddPatchPath(paths, line["rename to ".Length..]);
            }
            else if (line.StartsWith("copy from ", StringComparison.Ordinal))
            {
                AddSourcePatchPath(paths, line["copy from ".Length..]);
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

    /// <summary>
    ///     Adds the SOURCE side of a rename or copy, refusing it when it names a secret.
    ///     <para>
    ///         Rename and copy are the only patch operations that move bytes the model has never seen. Everything else
    ///         in a unified diff carries its content as literal <c>+</c> lines, so a patch can only write a secret the
    ///         model already knew — whereas <c>rename from .env to notes.txt</c> relocates an unread credential to a
    ///         readable name, and <c>read_file("notes.txt")</c> then completes the leak the read gate just closed.
    ///     </para>
    ///     <para>
    ///         Only the source side is checked, on purpose. Gating the destination too would refuse CREATING
    ///         <c>.env.example</c> (it matches the <c>.env.*</c> rule), which is ordinary, legitimate work — and a
    ///         creation has no secret source to leak.
    ///     </para>
    /// </summary>
    private void AddSourcePatchPath(HashSet<string> paths, string path)
    {
        AddPatchPath(paths, path);
        if (IsSecretPath(path))
        {
            // Names only the path the patch itself supplied — no file content, and the patch is refused whole.
            throw new DevelopmentWorkspaceSecurityException($"a patch cannot rename or copy from '{path}' because files with that name commonly hold credentials.");
        }
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
