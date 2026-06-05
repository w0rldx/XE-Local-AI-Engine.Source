namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

/// <summary>
///     workspace copy <see cref="IAgentHomeWorkspaceService" />. For each trusted selected folder it walks the real host
///     tree once to plan the copy (applying the sensitive-file exclusions, resolving symlinks/reparse points against
///     the canonical root, and summing surviving bytes), blocks a folder that exceeds the byte budget, then copies the
///     survivors into <c>/agent-home/workspace/selected/&lt;alias&gt;</c> through the sandbox provider. After at least
///     one folder copies it creates a temporary in-sandbox git baseline that patch export diffs against. The
///     model-facing result carries aliases and counts only — never host paths.
/// </summary>
internal sealed class AgentHomeWorkspaceService : IAgentHomeWorkspaceService
{
    private const string BaselineUserEmail = "agent-home@localhost";
    private const string BaselineUserName = "AgentHome";

    private readonly ISensitiveFileExclusionService _exclusionService;
    private readonly ILogger<AgentHomeWorkspaceService> _logger;
    private readonly AgentHomeOptions _options;
    private readonly ISandboxRuntimeProvider _provider;

    public AgentHomeWorkspaceService(ISandboxRuntimeProvider provider,
        ISensitiveFileExclusionService exclusionService,
        IOptions<AgentHomeOptions> options,
        ILogger<AgentHomeWorkspaceService> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _exclusionService = exclusionService ?? throw new ArgumentNullException(nameof(exclusionService));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<SelectedFolderSnapshot>> PrepareSelectedFoldersAsync(SandboxHandle handle,
        IReadOnlyList<ResolvedSelectedFolder> resolvedFolders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(resolvedFolders);

        if (resolvedFolders.Count == 0)
        {
            return [];
        }

        // The folders own a non-thread-safe walk over distinct host roots; copy them sequentially and honor cancel
        // between folders (the await-loop is S3267-exempt — there is no LINQ projection of an asynchronous copy).
        var snapshots = new List<SelectedFolderSnapshot>(resolvedFolders.Count);
        var anyFileCopied = false;
        foreach (var folder in resolvedFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await CopyFolderAsync(handle, folder, cancellationToken).ConfigureAwait(false);
            snapshots.Add(snapshot);
            anyFileCopied |= snapshot is { Status: SelectedFolderCopyStatus.Copied, CopiedFileCount: > 0 };
        }

        if (anyFileCopied)
        {
            await CreateGitBaselineAsync(handle, cancellationToken).ConfigureAwait(false);
        }

        return snapshots;
    }

    private async Task<SelectedFolderSnapshot> CopyFolderAsync(SandboxHandle handle,
        ResolvedSelectedFolder folder,
        CancellationToken cancellationToken)
    {
        var root = HostPathSafety.TryResolveTrustedRoot(folder.HostPath)
                   ?? throw new AgentHomeRequestRejectedException($"selected folder '{folder.Alias}' could not be resolved to a safe host path.");

        if (folder.Mode == SelectedFolderMode.ReadOnlyMount)
        {
            // The current sandbox providers do not support read-only mounts; copy instead.
            _logger.LogInformation("Selected folder {Alias} requested a read-only mount; copying instead (no provider mount support).",
                folder.Alias);
        }

        var plan = BuildCopyPlan(root, folder.Alias, cancellationToken);
        var workspacePath = RelativeWorkspacePath(folder.Alias);

        if (plan.TotalBytes > _options.MaxSelectedFolderBytes)
        {
            _logger.LogWarning("Selected folder {Alias} is {Bytes} bytes, over the {Budget}-byte budget; copy blocked.",
                folder.Alias,
                plan.TotalBytes,
                _options.MaxSelectedFolderBytes);

            return new SelectedFolderSnapshot
            {
                Alias = folder.Alias,
                Status = SelectedFolderCopyStatus.BlockedQuota,
                CopiedFileCount = 0,
                ExcludedFileCount = plan.ExcludedFileCount,
                ExcludedDirectoryCount = plan.ExcludedDirectoryCount,
                CopiedBytes = 0,
                WorkspacePath = workspacePath
            };
        }

        var sandboxDestinationRoot = $"{AgentHomeGit.WorkspaceSelectedRoot}/{folder.Alias}";
        foreach (var file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = $"{sandboxDestinationRoot}/{file.RelativePosixPath}";
            await _provider.CopyIntoAsync(handle,
                new SandboxCopyRequest
                {
                    SourcePath = file.HostPath,
                    DestinationPath = destination
                },
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Copied selected folder {Alias}: {CopiedFiles} file(s), {CopiedBytes} byte(s); excluded {ExcludedFiles} file(s) and {ExcludedDirs} directory(ies).",
            folder.Alias,
            plan.Files.Count,
            plan.TotalBytes,
            plan.ExcludedFileCount,
            plan.ExcludedDirectoryCount);

        return new SelectedFolderSnapshot
        {
            Alias = folder.Alias,
            Status = SelectedFolderCopyStatus.Copied,
            CopiedFileCount = plan.Files.Count,
            ExcludedFileCount = plan.ExcludedFileCount,
            ExcludedDirectoryCount = plan.ExcludedDirectoryCount,
            CopiedBytes = plan.TotalBytes,
            WorkspacePath = workspacePath
        };
    }

    private CopyPlan BuildCopyPlan(string root, string alias, CancellationToken cancellationToken)
    {
        var files = new List<CopyFile>();
        var totalBytes = 0L;
        var excludedFiles = 0;
        var excludedDirectories = 0;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (HostPathSafety.IsReparsePoint(info))
                {
                    HandleReparseEntry(info, root, alias);
                    if ((info.Attributes & FileAttributes.Directory) != 0)
                    {
                        excludedDirectories++;
                    }
                    else
                    {
                        excludedFiles++;
                    }

                    continue;
                }

                var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                if (_exclusionService.IsExcluded(info.Name, isDirectory))
                {
                    if (isDirectory)
                    {
                        excludedDirectories++;
                    }
                    else
                    {
                        excludedFiles++;
                    }

                    continue;
                }

                if (isDirectory)
                {
                    pending.Push(info.FullName);
                    continue;
                }

                if (info is not FileInfo file || !File.Exists(file.FullName))
                {
                    // Non-regular file (socket — e.g. a Docker socket — fifo, or device): never copied.
                    excludedFiles++;
                    continue;
                }

                var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                files.Add(new CopyFile(file.FullName, relative, file.Length));
                totalBytes += file.Length;
            }
        }

        return new CopyPlan(files, totalBytes, excludedFiles, excludedDirectories);
    }

    private static void HandleReparseEntry(FileSystemInfo info, string root, string alias)
    {
        // A reparse point that cannot be resolved, or whose target escapes the trusted root, is an attack signal:
        // fail closed for the whole prepare. A within-root link is skipped — its real target is
        // already covered by the direct walk, and skipping avoids cycles and duplicate copies.
        if (!HostPathSafety.TryResolveReparseWithinRoot(info, root, out var withinRoot))
        {
            throw new AgentHomeRequestRejectedException($"selected folder '{alias}' contains a link that cannot be resolved safely.");
        }

        if (!withinRoot)
        {
            throw new AgentHomeRequestRejectedException($"selected folder '{alias}' contains a link that escapes the folder.");
        }
    }

    private async Task CreateGitBaselineAsync(SandboxHandle handle, CancellationToken cancellationToken)
    {
        // The baseline must be captured after copy and before any agent edit, so it lives in preparation (workspace copy),
        // not in the run-time patch export, which runs after the agent has changed files. The hardened git
        // byte-stabilizing flags (hooks/attributes disabled, autocrlf/filemode off) make the later diff reproducible
        // even if a copied .gitattributes would otherwise perturb the bytes; the baseline must use the same flags the
        // diff is taken under. --allow-empty keeps an all-ignored tree (a copied .gitignore that hides every file) from
        // failing the commit and sinking the whole prepare. On the fake provider these are scripted no-ops; real git
        // state arrives with the HostAgent-backed local-container provider.
        var timeout = TimeSpan.FromSeconds(_options.PrepareTimeoutSeconds);
        var commands = new[]
        {
            BaselineCommand("agent-home-baseline-init", timeout, AgentHomeGit.Arguments("init")),
            BaselineCommand("agent-home-baseline-autocrlf", timeout, AgentHomeGit.Arguments("config", "core.autocrlf", "false")),
            BaselineCommand("agent-home-baseline-filemode", timeout, AgentHomeGit.Arguments("config", "core.filemode", "false")),
            BaselineCommand("agent-home-baseline-add", timeout, AgentHomeGit.Arguments("add", "-A")),
            BaselineCommand("agent-home-baseline-commit", timeout, AgentHomeGit.Arguments("-c",
                $"user.email={BaselineUserEmail}",
                "-c",
                $"user.name={BaselineUserName}",
                "commit",
                "-m",
                "agent-home baseline",
                "--allow-empty"))
        };

        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _provider.ExecuteAsync(handle, command, cancellationToken).ConfigureAwait(false);
            if (!result.Completed || result.ExitCode != 0)
            {
                // A failed baseline command leaves no reproducible HEAD for the patch export diff to compare against, so
                // fail the prepare loudly rather than letting a later export silently report zero changes. The message
                // carries only the command's execution id and exit code — never a host path. (Real non-zero git exits
                // arrive with the local-container provider in local-container sandbox.)
                throw new AgentHomeRequestRejectedException($"the in-sandbox git baseline command '{command.ExecutionId}' failed (exit code {result.ExitCode}).");
            }
        }

        _logger.LogInformation("Created the in-sandbox git baseline for the selected workspace.");
    }

    private static SandboxCommandRequest BaselineCommand(string executionId, TimeSpan timeout, IReadOnlyList<string> arguments)
    {
        return new SandboxCommandRequest
        {
            ExecutionId = executionId,
            Executable = AgentHomeGit.Executable,
            Arguments = arguments,
            WorkingDirectory = AgentHomeGit.WorkspaceSelectedRoot,
            Timeout = timeout
        };
    }

    private static string RelativeWorkspacePath(string alias)
    {
        return $"workspace/selected/{alias}";
    }

    private sealed record CopyFile(string HostPath, string RelativePosixPath, long Length);

    private sealed record CopyPlan(
        IReadOnlyList<CopyFile> Files,
        long TotalBytes,
        int ExcludedFileCount,
        int ExcludedDirectoryCount);
}
