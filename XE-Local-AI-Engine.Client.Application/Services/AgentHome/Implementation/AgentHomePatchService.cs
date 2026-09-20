namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Patch export implementation for <see cref="IAgentHomePatchService" />.
/// </summary>
/// <remarks>
///     It runs two in-sandbox <c>git diff</c> commands against the workspace-copy baseline with the byte-stabilizing flags — a full
///     <c>--binary</c> patch and a <c>--name-status</c> summary — under <see cref="AgentHomeGitHardening" />, which keeps a model-authored
///     <c>textconv</c>/<c>clean</c> driver from executing here after the model's turn has ended. The worker captures their standard output
///     and owns the file write, since the sandbox SPI carries no shell redirection, then writes <c>changes.patch</c> and <c>changed-files.json</c>
///     under <c>runs/&lt;run-id&gt;/patches/</c>, bounded by <see cref="AgentHomeOptions.MaxPatchBytes" />. Model-facing paths stay run-relative.
/// </remarks>
internal sealed class AgentHomePatchService : IAgentHomePatchService
{
    private static readonly JsonSerializerOptions ChangedFilesJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private readonly ILogger<AgentHomePatchService> _logger;
    private readonly IAgentSandboxRuntimeProvider _provider;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly TimeProvider _timeProvider;

    public AgentHomePatchService(IAgentSandboxRuntimeProvider provider,
        INodeRuntimeSettings runtimeSettings,
        TimeProvider timeProvider,
        ILogger<AgentHomePatchService> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AgentHomePatchExport> ExportPatchAsync(SandboxHandle handle,
        AgentHomePatchExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var commandTimeoutSeconds = await _runtimeSettings.GetAgentHomeCommandTimeoutSecondsAsync(cancellationToken);
        var commandTimeout = TimeSpan.FromSeconds(commandTimeoutSeconds);

        // BEFORE any git runs here: make every configuration git can reach node-owned again, because the goal loop could
        // have planted a driver git would run HERE as the node. Fails closed if the repository is no longer the node's.
        if (!await AgentHomeGitHardening.TryHardenWorkspaceRepositoryAsync(handle, cancellationToken))
        {
            _logger.LogError("Patch export for run {RunId} refused: the workspace git directory is not the one the baseline created.",
                request.RunId);
            return FailedExport();
        }

        // Full binary-aware patch, captured from standard output because the SPI carries no shell redirection.
        // --no-textconv/--no-ext-diff are belt and braces on the rewrite above, not the control: a clean filter survives them.
        var patchResult = await RunGitAsync(handle,
            request,
            $"{request.RunId}-patch-diff",
            commandTimeout,
            ["diff", "--no-textconv", "--no-ext-diff", "--binary", "--find-renames=50%", "--find-copies=50%", "--src-prefix=a/", "--dst-prefix=b/", "HEAD", "--", "."],
            cancellationToken);

        // Name-status summary used to build changed-files.json.
        var statusResult = await RunGitAsync(handle,
            request,
            $"{request.RunId}-patch-status",
            commandTimeout,
            ["diff", "--no-textconv", "--no-ext-diff", "--name-status", "--find-renames=50%", "--find-copies=50%", "HEAD", "--", "."],
            cancellationToken);

        if (!IsSuccessful(patchResult) || !IsSuccessful(statusResult))
        {
            // A non-zero exit or an incomplete command means no patch could be produced: surface that distinctly, so it is
            // not read as a clean zero-change run, and write no artifacts. Real non-zero git exits need a real provider.
            _logger.LogWarning("Patch export for run {RunId} aborted: patch diff exit {PatchExit} (completed {PatchCompleted}), name-status exit {StatusExit} (completed {StatusCompleted}).",
                request.RunId,
                patchResult.ExitCode,
                patchResult.Completed,
                statusResult.ExitCode,
                statusResult.Completed);
            return FailedExport();
        }

        var changedFiles = ParseChangedFiles(statusResult.StandardOutput, request.ResolvedFolders);
        if (changedFiles.Count == 0)
        {
            // Baseline == workspace: nothing changed, so write neither artifact (no patches/ directory is created).
            return EmptyExport();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var patchesDirectory = Path.Combine(request.HostRunDirectory, "patches");
        Directory.CreateDirectory(patchesDirectory);

        var changedFilesRelative = RunRelativePath(request.RunId, "changed-files.json");
        var changedFilesJson = JsonSerializer.Serialize(changedFiles, ChangedFilesJsonOptions);
        await File.WriteAllTextAsync(Path.Combine(patchesDirectory, "changed-files.json"), changedFilesJson, cancellationToken);

        var patchText = patchResult.StandardOutput;
        var patchBytes = Encoding.UTF8.GetByteCount(patchText);
        var maxPatchBytes = await _runtimeSettings.GetAgentHomeMaxPatchBytesAsync(cancellationToken);
        if (patchBytes > maxPatchBytes)
        {
            // Over budget: keep the changed-file metadata, drop the oversized patch.
            _logger.LogWarning("Patch for run {RunId} is {Bytes} byte(s), over the {Budget}-byte budget; changes.patch not written.",
                request.RunId,
                patchBytes,
                maxPatchBytes);

            return new AgentHomePatchExport
            {
                ChangedFileCount = changedFiles.Count,
                Blocked = true,
                PatchBytes = patchBytes,
                PatchRelativePath = null,
                ChangedFilesRelativePath = changedFilesRelative
            };
        }

        await File.WriteAllTextAsync(Path.Combine(patchesDirectory, "changes.patch"), patchText, cancellationToken);

        _logger.LogInformation("Exported patch for run {RunId}: {ChangedCount} changed file(s), {Bytes} byte(s).",
            request.RunId,
            changedFiles.Count,
            patchBytes);

        return new AgentHomePatchExport
        {
            ChangedFileCount = changedFiles.Count,
            Blocked = false,
            PatchBytes = patchBytes,
            PatchRelativePath = RunRelativePath(request.RunId, "changes.patch"),
            ChangedFilesRelativePath = changedFilesRelative
        };
    }

    /// <summary>
    ///     Runs one export git command and appends it to the run's command log, attributed to the NODE.
    /// </summary>
    /// <remarks>
    ///     The log entry is not decoration: this git runs in the same sandbox, over the same workspace, as the
    ///     model's own <c>run_command</c> calls, and an operator auditing <c>commands.jsonl</c> after a run has to
    ///     see the whole sequence rather than only the half the model asked for.
    /// </remarks>
    private async Task<SandboxCommandResult> RunGitAsync(SandboxHandle handle,
        AgentHomePatchExportRequest request,
        string executionId,
        TimeSpan timeout,
        string[] tail,
        CancellationToken cancellationToken)
    {
        var arguments = AgentHomeGit.WorkspaceArguments(tail);
        var startedAt = _timeProvider.GetUtcNow();
        var result = await _provider.ExecuteAsync(handle,
            new SandboxCommandRequest
            {
                ExecutionId = executionId,
                Executable = AgentHomeGit.Executable,
                Arguments = arguments,
                WorkingDirectory = AgentHomeGit.WorkspaceSelectedRoot,
                Environment = AgentHomeGitHardening.Environment,
                Timeout = timeout
            },
            cancellationToken);

        await AppendCommandSafelyAsync(request, executionId, arguments, result, startedAt, cancellationToken);
        return result;
    }

    private async Task AppendCommandSafelyAsync(AgentHomePatchExportRequest request,
        string executionId,
        IReadOnlyList<string> arguments,
        SandboxCommandResult result,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await request.RunLogger.AppendCommandAsync(new AgentHomeCommandLogRecord
                {
                    TimestampUtc = startedAt,
                    ExecutionId = executionId,
                    Executable = AgentHomeGit.Executable,
                    Arguments = arguments,
                    Completed = result.Completed,
                    ExitCode = result.ExitCode,
                    DurationMs = (long)result.Duration.TotalMilliseconds,
                    ErrorClass = null,
                    Actor = AgentHomeCommandActors.Node
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Best-effort logging: a filesystem, permissions or not-opened error must never fail the export.
            _logger.LogDebug(exception, "Patch export for run {RunId} could not append its git command to the run log.", request.RunId);
        }
    }

    private static bool IsSuccessful(SandboxCommandResult result)
    {
        return result.Completed && result.ExitCode == 0;
    }

    private List<ChangedFileEntry> ParseChangedFiles(string nameStatusOutput,
        IReadOnlyList<ResolvedSelectedFolder> resolvedFolders)
    {
        // Defensive group-by-alias: the selected-folder store's unique alias index makes a duplicate unreachable, but
        // grouping (take-first) keeps a should-never-happen collision from throwing during export.
        var aliasToId = resolvedFolders
                        .GroupBy(folder => folder.Alias, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.First().Id.ToString(), StringComparer.Ordinal);

        return nameStatusOutput
               .Split('\n')
               .Select(line => line.TrimEnd('\r'))
               .Where(line => line.Length > 0)
               .Select(line => TryMapEntry(line, aliasToId))
               .OfType<ChangedFileEntry>()
               .ToList();
    }

    private ChangedFileEntry? TryMapEntry(string line, IReadOnlyDictionary<string, string> aliasToId)
    {
        var fields = line.Split('\t');
        if (fields.Length < 2)
        {
            return null;
        }

        var status = fields[0];
        var path = ResolveChangedPath(status, fields);

        var separator = path.IndexOf(value: '/', StringComparison.Ordinal);
        if (separator <= 0)
        {
            _logger.LogWarning("Skipping a changed-file entry that has no alias segment.");
            return null;
        }

        var alias = path[..separator];
        var relativePath = path[(separator + 1)..];
        if (!aliasToId.TryGetValue(alias, out var folderId))
        {
            _logger.LogWarning("Skipping a changed-file entry for an alias not in the prepared workspace.");
            return null;
        }

        return new ChangedFileEntry
        {
            SelectedFolderId = folderId,
            Alias = alias,
            RelativePath = relativePath,
            ChangeType = MapChangeType(status)
        };
    }

    private static string ResolveChangedPath(string status, string[] fields)
    {
        // Rename/copy lines carry "<status>\t<old>\t<new>"; the destination (new) path is the changed file.
        if ((status.StartsWith('R') || status.StartsWith('C')) && fields.Length >= 3)
        {
            return fields[2];
        }

        return fields[1];
    }

    private static string MapChangeType(string status)
    {
        if (status.Length == 0)
        {
            return "unknown";
        }

        return status[0] switch
        {
            'A' => "added",
            'M' => "modified",
            'D' => "deleted",
            'R' => "renamed",
            'C' => "copied",
            'T' => "typechanged",
            'U' => "unmerged",
            _ => "unknown"
        };
    }

    private static string RunRelativePath(string runId, string fileName)
    {
        return $"runs/{runId}/patches/{fileName}";
    }

    private static AgentHomePatchExport EmptyExport()
    {
        return new AgentHomePatchExport
        {
            ChangedFileCount = 0,
            Blocked = false,
            PatchBytes = 0,
            PatchRelativePath = null,
            ChangedFilesRelativePath = null
        };
    }

    private static AgentHomePatchExport FailedExport()
    {
        return new AgentHomePatchExport
        {
            ChangedFileCount = 0,
            Blocked = false,
            Failed = true,
            PatchBytes = 0,
            PatchRelativePath = null,
            ChangedFilesRelativePath = null
        };
    }
}
