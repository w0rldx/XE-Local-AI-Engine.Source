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
    /// <summary>The artifact <em>protocol</em> version for coder-produced evidence.</summary>
    /// <remarks>
    ///     Not the command-profile version: it describes the shape of the artifacts this class's caller writes, and
    ///     the apply and reviewer gates compare their own protocol constants against it. Keep it separate from
    ///     <see cref="DevelopmentCommandProfile.ComputeDigest" />.
    /// </remarks>
    private const string CommandProfileVersion = "development-workspace-v1";

    /// <summary>The product's single definition of "this file may hold a credential".</summary>
    /// <remarks>
    ///     Read paths call its <see cref="ISensitiveFileExclusionService.IsSecret" /> predicate, never the broader
    ///     <see cref="ISensitiveFileExclusionService.IsExcluded" /> copy filter, which also names build output an
    ///     agent legitimately reads after a failed build. This is a MITIGATION, not a boundary: it closes the
    ///     one-step path of a model naming a secret to <c>read_file</c>, and nothing more — Development Mode also
    ///     executes the repository's tests, and one printing <c>.env</c> puts those bytes in captured stdout.
    /// </remarks>
    private static readonly ISensitiveFileExclusionService DefaultExclusions = new SensitiveFileExclusionService();

    private readonly List<DevelopmentCommandEvidence> _commandEvidence = [];

    /// <summary>
    ///     The import file's digest as the worktree presented it before this attempt ran anything, or null if absent.
    /// </summary>
    /// <remarks>
    ///     Captured here rather than taken from <see cref="DevelopmentCommandProfile.ImportDigest" />, which was
    ///     recorded at project creation from the operator's live working tree while the managed worktree sits at the
    ///     base commit, so an uncommitted edit would fail every attempt on its first command. The invariant needs the
    ///     narrower claim that nothing THIS attempt ran changed the file. Taken in the constructor, after the
    ///     workspace is prepared and validated and before any command executes.
    /// </remarks>
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
    ///     Lists the workspace's regular files, served by <see cref="WorkspaceFileScanner" /> rather than <c>find</c>.
    /// </summary>
    /// <remarks>
    ///     The suppression predicate is applied at the PRUNE step, not only to the finished list, which is what makes
    ///     the listing usable at all: the output budget is spent before any post-filter runs, so on a workspace whose
    ///     Git metadata alone outruns it every surviving entry names a suppressed path and the answer is empty while
    ///     the workspace is full of files the agent could act on.
    /// </remarks>
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
            cancellationToken);
    }

    /// <summary>
    ///     Searches the workspace for a LITERAL string, served by <see cref="WorkspaceFileScanner" /> not <c>grep</c>.
    /// </summary>
    /// <remarks>
    ///     Credential-bearing entries are excluded by PRUNING, so a secret's content never enters the result and the
    ///     emitted line is never produced rather than filtered afterwards; build output stays searchable, being
    ///     legitimate and leaking nothing. The pattern is matched ordinally as a fixed string and never compiled as a
    ///     regular expression, so a model-supplied value cannot be read as a flag or backtrack catastrophically.
    /// </remarks>
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
    ///     Runs one of the two managed workspace surveys against the confined host directory, under
    ///     <see cref="DevelopmentOptions.ToolCommandTimeoutSeconds" /> so a pathological tree cannot consume the
    ///     whole attempt.
    /// </summary>
    /// <remarks>
    ///     It reads the HOST worktree rather than routing through the sandbox, as the engine already does for its own
    ///     workspace invariants and for evidence export: every provider's workspace is that same directory, so the
    ///     bytes surveyed are the bytes a command inside the sandbox would see. The failure sentence is kept
    ///     identical to <see cref="EnsureCompleted" />'s, because the task output and the RC runbook both name it.
    /// </remarks>
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
            throw new DevelopmentWorkspaceSecurityException(exception.Message, exception);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The fixed Development {operation} operation failed.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // No inner exception on purpose: the filesystem exception's message carries the host worktree path, and
            // this failure is rendered into a tool result the sandboxed model reads.
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
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
            await _sandbox.CopyIntoAsync(_session.SandboxHandle,
                new SandboxCopyRequest
                {
                    SourcePath = tempPath,
                    DestinationPath = confined.SandboxPath
                },
                cancellationToken);
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
                _ = await _sandbox.ReadFileAsync(_session.SandboxHandle, confined.SandboxPath, cancellationToken);
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
            cancellationToken);
        EnsureCompleted(check, "apply_patch check");
        var apply = await ExecuteAsync("tool_apply_patch",
            AgentHomeGit.Executable,
            AgentHomeGit.Arguments("apply", "--whitespace=error-all", "-"),
            "/",
            patch,
            cancellationToken);
        EnsureCompleted(apply, "apply_patch");
        foreach (var path in paths)
        {
            _liveProgress?.FileChanged(path, patchBytes);
        }

        return $"applied patch for {paths.Count} path(s)";
    }

    public async Task<string> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCatalogAsync(DevelopmentCommandIds.GitStatus, cancellationToken);
        return result.StandardOutput;
    }

    /// <summary>
    ///     The worktree against the BASE COMMIT, the one comparison the submission contract, the patch evidence and
    ///     this tool all have to agree on.
    /// </summary>
    /// <remarks>
    ///     Diffing against the index instead answers "nothing changed" for every file an earlier attempt on the
    ///     shared workspace left behind, because the index equals the worktree from the moment an attempt starts —
    ///     and those are exactly the files the prompt points a later attempt at. Files this attempt CREATED are
    ///     untracked and still absent here; <c>get_status</c> has them.
    /// </remarks>
    public async Task<string> GetDiffAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync("tool_git_diff",
            AgentHomeGit.Executable,
            AgentHomeGit.Arguments("diff", "--binary", "HEAD", "--", "."),
            "/",
            standardInput: null,
            cancellationToken);
        EnsureCompleted(result, "git diff");
        return result.StandardOutput;
    }

    public async Task<string> RunCommandAsync(string commandId, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCatalogAsync(commandId, cancellationToken);
        return $"{commandId}: exit={result.ExitCode}, completed={result.Completed}\n{result.StandardOutput}\n{result.StandardError}";
    }

    internal const string ProfileVersion = CommandProfileVersion;

    private async Task<SandboxCommandResult> ExecuteCatalogAsync(string commandId, CancellationToken cancellationToken)
    {
        var command = _profile.ResolveCommand(commandId);

        _liveProgress?.CommandStarted(commandId);

        // Raw first, truncated second, and the result adapter reads the raw copy: a runner prints its summary LAST
        // and Truncate keeps the HEAD, so the evidence copy loses it on any sufficiently verbose repository.
        var raw = await ExecuteRawAsync(commandId,
            command.Executable,
            command.Arguments,
            "/",
            standardInput: null,
            ResolveTimeout(command.TimeoutSeconds),
            cancellationToken);
        var testOutcome = DevelopmentTestResultAdapters.Resolve(_profile, commandId)
                                                       ?.Parse(raw.StandardOutput,
                                                           raw.StandardError,
                                                           raw.StandardOutputTruncated || raw.StandardErrorTruncated);

        var result = TruncateForEvidence(raw);
        await EnsureWorkspaceInvariantAsync(cancellationToken);
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
            cancellationToken);
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
            cancellationToken);
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
    ///     Re-checks the repository's <c>.xe-dev/profile.json</c> against the imported digest, after every catalog
    ///     command.
    /// </summary>
    /// <remarks>
    ///     <see cref="DevelopmentWorkspaceSecurity" />'s deny list only stops the agent naming that path as a tool
    ///     argument; it does nothing about a build or test command writing the file as a side effect, which is what
    ///     this check closes and why the deny-list entry must not be mistaken for the guard. Read from the host
    ///     worktree, because routing an invariant check through the surface being verified would be circular.
    /// </remarks>
    private void EnsureCommandProfileImportUnchanged()
    {
        var actual = DevelopmentCommandProfileImport.TryComputeDigest(_session.HostWorktreePath);
        if (!string.Equals(actual, _importBaselineDigest, StringComparison.Ordinal))
        {
            throw new DevelopmentWorkspaceSecurityException("A fixed command changed the repository command-profile import file in the managed Development worktree.");
        }
    }

    /// <summary>
    ///     Runs one of the engine's own fixed helper commands: directory listing, text search, patch application,
    ///     diff, and the post-command workspace invariant probes.
    /// </summary>
    /// <remarks>
    ///     Bounded by <see cref="DevelopmentOptions.ToolCommandTimeoutSeconds" /> rather than by the attempt cap, or
    ///     each could individually consume the whole attempt budget and a hung search would be indistinguishable from
    ///     a legitimately long build.
    /// </remarks>
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
            cancellationToken));

    /// <summary>
    ///     Runs a command and returns its output as the sandbox produced it, capped only by the sandbox's own stream
    ///     limit.
    /// </summary>
    /// <remarks>
    ///     Callers that persist the result must pass it through <see cref="TruncateForEvidence" /> first. The only
    ///     reason to hold the untruncated form is to read structure out of it, the evidence cap keeping the head
    ///     while the structure a test runner emits is at the tail.
    /// </remarks>
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
    ///     Clamps a per-command budget to the attempt cap.
    /// </summary>
    /// <remarks>
    ///     A profile can ask for less than the attempt allows but never more, so
    ///     <see cref="DevelopmentOptions.MaxAttemptDurationSeconds" /> stays the outer bound it claims to be.
    /// </remarks>
    private TimeSpan ResolveTimeout(int requestedSeconds) =>
        TimeSpan.FromSeconds(Math.Min(Math.Max(requestedSeconds, 1), _options.MaxAttemptDurationSeconds));

    /// <summary>
    ///     The environment every sandboxed command runs under, with every path expressed in the SANDBOX's namespace
    ///     rather than the host's.
    /// </summary>
    /// <remarks>
    ///     Absolute host paths work only while the child runs on the host: inside a container none of them exist and
    ///     the root filesystem is read-only, so every <c>dotnet</c> command fails, and fails obscurely, naming a
    ///     directory the container has never heard of. The mapping comes from the sandbox handle, so the process
    ///     provider, which identity-maps, still emits host paths.
    /// </remarks>
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
            // MSBuild's reusable worker nodes outlive the dotnet process that started them and carry the per-task
            // NUGET_PACKAGES into a later restore on the same host, which then writes a deleted path (NU5037, CS0006).
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_HOME"] = ResolveRuntimeDirectory("dotnet"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            // Or the CLI's first-run appends the per-task DOTNET_CLI_HOME tools directory to the PERSISTED per-user PATH,
            // one dead entry per task, until cmd.exe gets an EMPTY %PATH%. DOTNET_SKIP_FIRST_TIME_EXPERIENCE is a .NET 10 no-op.
            ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "0"
        };
    }

    /// <summary>
    ///     Translates one per-task runtime directory into the path that names it inside the sandbox.
    /// </summary>
    /// <remarks>
    ///     It throws rather than falling back to the host path when no mount covers it: a fallback produces a command
    ///     that looks correct and fails deep inside a build against a directory that does not exist, which is harder
    ///     to diagnose than a refusal naming the missing mount.
    /// </remarks>
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

    /// <summary>Refuses a read whose path names a credential-bearing entry.</summary>
    /// <remarks>
    ///     See <see cref="DefaultExclusions" /> for what this does and does not close: it removes the direct read and
    ///     nothing else. It gates on <see cref="ISensitiveFileExclusionService.IsSecret" />, never the broader copy
    ///     filter, so build output stays readable — reading it after a failed build is a primary reason this feature
    ///     exists, and none of it is a credential.
    /// </remarks>
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
    ///     What <c>list_files</c> and <c>search_text</c> neither descend into nor emit: credentials, plus the paths
    ///     <see cref="DevelopmentWorkspaceSecurity.Confine" /> already refuses as tool arguments.
    /// </summary>
    /// <remarks>
    ///     Build output is deliberately in neither, being neither secret nor protected. This one predicate is the
    ///     whole rule, <see cref="WorkspaceFileScanner" /> consulting it to prune a directory and again to admit a
    ///     file, so no separately built expression can drift from it. Secrets match by NAME at any depth while
    ///     protected trees match rooted at the SCANNED directory, so a subdirectory listing still suppresses secrets.
    /// </remarks>
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

    /// <summary>Adds the SOURCE side of a rename or copy, refusing it when it names a secret.</summary>
    /// <remarks>
    ///     Rename and copy are the only patch operations that move bytes the model has never seen; everything else in
    ///     a unified diff carries its content as literal <c>+</c> lines, so a patch can only write a secret the model
    ///     already knew. Renaming <c>.env</c> to a readable name and then reading it completes the leak the read gate
    ///     closed. Only the source side is checked, or creating <c>.env.example</c> — legitimate work with no secret
    ///     source — would be refused too.
    /// </remarks>
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
