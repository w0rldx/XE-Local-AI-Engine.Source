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
    string StandardError);

internal interface IDevelopmentWorkspaceTools
{
    IReadOnlyList<DevelopmentCommandEvidence> CommandEvidence { get; }
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
    private const string Solution = "XE-Local-AI-Engine.slnx";
    private const string CommandProfileVersion = "g004-v1";

    private readonly List<DevelopmentCommandEvidence> _commandEvidence = [];
    private readonly DevelopmentOptions _options;
    private readonly ISandboxRuntimeProvider _sandbox;
    private readonly DevelopmentWorkspaceSession _session;

    public DevelopmentWorkspaceTools(ISandboxRuntimeProvider sandbox,
        DevelopmentWorkspaceSession session,
        IOptions<DevelopmentOptions> options)
    {
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        EnsureRuntimeDirectories();
    }

    public IReadOnlyList<DevelopmentCommandEvidence> CommandEvidence => _commandEvidence;

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
        var content = await _sandbox.ReadFileAsync(_session.SandboxHandle, confined.SandboxPath, cancellationToken).ConfigureAwait(false);
        return Truncate(content, _options.MaxCommandOutputBytes);
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
                new SandboxCopyRequest { SourcePath = tempPath, DestinationPath = confined.SandboxPath },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(tempPath);
        }

        return $"wrote {bytes.Length} byte(s) to {confined.RelativePath}";
    }

    public async Task<string> ApplyPatchAsync(string patch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (Encoding.UTF8.GetByteCount(patch) > _options.MaxPatchBytes)
        {
            throw new InvalidOperationException("The requested patch exceeds the configured Development patch limit.");
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
        var command = commandId switch
        {
            DevelopmentCommandIds.GitStatus => (AgentHomeGit.Executable, AgentHomeGit.Arguments("status", "--short", "--branch", "--untracked-files=all", "--", ".")),
            DevelopmentCommandIds.GitDiffCheck => (AgentHomeGit.Executable, AgentHomeGit.Arguments("diff", "--check", "HEAD", "--", ".")),
            DevelopmentCommandIds.DotnetRestore => ("dotnet", (IReadOnlyList<string>)["restore", Solution]),
            DevelopmentCommandIds.DotnetBuildRelease => ("dotnet", (IReadOnlyList<string>)["build", Solution, "--configuration", "Release", "--no-restore"]),
            DevelopmentCommandIds.DotnetTestRelease => ("dotnet", (IReadOnlyList<string>)["test", Solution, "--configuration", "Release", "--no-build", "--max-parallel-test-modules", "1"]),
            _ => throw new DevelopmentWorkspaceSecurityException("The requested command id is not in the code-owned Development command catalog.")
        };

        var result = await ExecuteAsync(commandId, command.Item1, command.Item2, "/", standardInput: null, cancellationToken).ConfigureAwait(false);
        await EnsureWorkspaceInvariantAsync(cancellationToken).ConfigureAwait(false);
        _commandEvidence.Add(new DevelopmentCommandEvidence(commandId,
            result.ExitCode,
            result.Completed,
            result.StandardOutputTruncated || result.StandardErrorTruncated,
            (long)result.Duration.TotalMilliseconds,
            result.StandardOutput,
            result.StandardError));
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
    }

    private async Task<SandboxCommandResult> ExecuteAsync(string executionPrefix,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var result = await _sandbox.ExecuteAsync(_session.SandboxHandle, new SandboxCommandRequest
        {
            ExecutionId = executionPrefix + "-" + Guid.NewGuid().ToString("N"),
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            StandardInput = standardInput,
            Environment = BuildEnvironment(),
            Timeout = TimeSpan.FromSeconds(_options.MaxAttemptDurationSeconds)
        }, cancellationToken).ConfigureAwait(false);
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
        foreach (var name in new[] { "home", "tmp", "nuget", "dotnet" })
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
        foreach (var line in patch.Split('\n'))
        {
            if (!line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 4 || !fields[2].StartsWith("a/", StringComparison.Ordinal) || !fields[3].StartsWith("b/", StringComparison.Ordinal))
            {
                throw new DevelopmentWorkspaceSecurityException("Quoted or ambiguous patch paths are not accepted by the bounded patch tool.");
            }

            paths.Add(fields[2][2..]);
            paths.Add(fields[3][2..]);
        }

        return paths;
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
