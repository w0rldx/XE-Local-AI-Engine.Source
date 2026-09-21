namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Globalization;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Patch export implementation for <see cref="IAgentHomePatchService" />.
/// </summary>
/// <remarks>
///     It stages the workspace with <c>add -A</c> — not <c>--force</c>, so <c>.gitignore</c> still decides what is offered, as it did for the baseline — then
///     runs two in-sandbox <c>git diff --cached</c> commands with the byte-stabilizing flags: a full <c>--binary</c> patch and a <c>--name-status</c>
///     summary, under <see cref="AgentHomeGitHardening" />, which keeps a model-authored <c>textconv</c>/<c>clean</c> driver from executing after the model's
///     turn has ended. Their standard output becomes <c>changes.patch</c> and <c>changed-files.json</c> under <c>runs/&lt;run-id&gt;/patches/</c>, bounded by
///     <see cref="AgentHomeOptions.MaxPatchBytes" />. Model-facing paths stay run-relative.
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

        // Stage the whole working tree first: a working-tree `diff HEAD` never sees an UNTRACKED path, so without this
        // every file the run CREATED was silently absent from both artifacts. Not --force — see the class summary.
        var stageResult = await RunGitAsync(handle,
            request,
            $"{request.RunId}-patch-stage",
            commandTimeout,
            ["add", "-A", "--", "."],
            cancellationToken);

        // Full binary-aware patch, captured from standard output because the SPI carries no shell redirection.
        // --no-textconv/--no-ext-diff are belt and braces on the rewrite above, not the control: a clean filter survives them.
        var patchResult = await RunGitAsync(handle,
            request,
            $"{request.RunId}-patch-diff",
            commandTimeout,
            ["diff", "--cached", "--no-textconv", "--no-ext-diff", "--binary", "--find-renames=50%", "--find-copies=50%", "--src-prefix=a/", "--dst-prefix=b/", "HEAD", "--", "."],
            cancellationToken);

        // Name-status summary used to build changed-files.json. -z is load-bearing: without it git C-quotes any
        // path holding a quote, a backslash, a tab or a newline, and a tab-delimited parse then cuts it in half.
        var statusResult = await RunGitAsync(handle,
            request,
            $"{request.RunId}-patch-status",
            commandTimeout,
            ["diff", "--cached", "--no-textconv", "--no-ext-diff", "--name-status", "-z", "--find-renames=50%", "--find-copies=50%", "HEAD", "--", "."],
            cancellationToken);

        if (!IsSuccessful(stageResult) || !IsSuccessful(patchResult) || !IsSuccessful(statusResult))
        {
            // A non-zero exit or an incomplete command means no patch could be produced: surface that distinctly, so it is
            // not read as a clean zero-change run, and write no artifacts. Real non-zero git exits need a real provider.
            _logger.LogWarning("Patch export for run {RunId} aborted: stage exit {StageExit} (completed {StageCompleted}), patch diff exit {PatchExit} (completed {PatchCompleted}), name-status exit {StatusExit} (completed {StatusCompleted}).",
                request.RunId,
                stageResult.ExitCode,
                stageResult.Completed,
                patchResult.ExitCode,
                patchResult.Completed,
                statusResult.ExitCode,
                statusResult.Completed);
            return FailedExport();
        }

        var changedFiles = ParseChangedFiles(statusResult.StandardOutput, request.ResolvedFolders);

        // Reconcile BEFORE the zero-change return: a run that wrote files and produced an empty diff is the loudest
        // form of the gap, and returning early would be exactly the silence this reconciliation exists to end.
        var writtenGap = await ReconcileWrittenFilesAsync(handle, request, statusResult.StandardOutput, commandTimeout, cancellationToken);

        if (changedFiles.Count == 0)
        {
            // Baseline == workspace: nothing changed, so write neither artifact (no patches/ directory is created).
            return EmptyExport(writtenGap);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var patchesDirectory = Path.Combine(request.HostRunDirectory, "patches");
        Directory.CreateDirectory(patchesDirectory);

        var changedFilesRelative = RunRelativePath(request.RunId, "changed-files.json");
        var changedFilesJson = JsonSerializer.Serialize(changedFiles, ChangedFilesJsonOptions);
        await File.WriteAllTextAsync(Path.Combine(patchesDirectory, "changed-files.json"), changedFilesJson, cancellationToken);

        var patchText = patchResult.StandardOutput;
        var patchBytes = Encoding.UTF8.GetByteCount(patchText);
        var (linesAdded, linesRemoved) = CountChangedLines(patchText);
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
                LinesAdded = linesAdded,
                LinesRemoved = linesRemoved,
                WrittenGap = writtenGap,
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
            LinesAdded = linesAdded,
            LinesRemoved = linesRemoved,
            WrittenGap = writtenGap,
            PatchRelativePath = RunRelativePath(request.RunId, "changes.patch"),
            ChangedFilesRelativePath = changedFilesRelative
        };
    }

    /// <summary>
    ///     Totals the lines the patch adds and removes by walking the text it already captured, so no further git
    ///     process runs.
    /// </summary>
    /// <remarks>
    ///     Only lines inside a hunk are counted, which is what makes the walk safe: the <c>---</c>/<c>+++</c> file
    ///     headers precede the first <c>@@</c>, a binary block never opens one at all, and a <c>\ No newline</c>
    ///     marker is neither an addition nor a removal. A binary or pure-rename block therefore contributes nothing,
    ///     matching git's own "not a line count" convention for them.
    /// </remarks>
    private static (int Added, int Removed) CountChangedLines(string patchText)
    {
        var added = 0;
        var removed = 0;
        var inHunk = false;

        foreach (var line in patchText.AsSpan().EnumerateLines())
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inHunk = false;
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
            }
            else if (inHunk && line.Length > 0 && line[0] == '+')
            {
                added++;
            }
            else if (inHunk && line.Length > 0 && line[0] == '-')
            {
                removed++;
            }
        }

        return (added, removed);
    }

    /// <summary>
    ///     Compares the paths the goal loop recorded writing with the paths the diff reports and classifies whatever
    ///     is missing, so a write the patch does not carry is recorded rather than lost.
    /// </summary>
    /// <remarks>
    ///     Never fails the export: an unclassifiable path counts as unexplained, which is the honest answer to "the
    ///     node cannot say where this went". The two classification commands run only when there IS a gap, so a run
    ///     whose writes all reached the patch costs no extra process.
    /// </remarks>
    private async Task<AgentHomeWrittenFileGap> ReconcileWrittenFilesAsync(SandboxHandle handle,
        AgentHomePatchExportRequest request,
        string nameStatusOutput,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        if (request.WrittenFiles.Count == 0)
        {
            return AgentHomeWrittenFileGap.None;
        }

        var exported = ExportedPaths(nameStatusOutput);
        var missing = request.WrittenFiles.Where(path => !exported.Contains(path)).ToList();
        if (missing.Count == 0)
        {
            return AgentHomeWrittenFileGap.None;
        }

        var ignored = await ResolveIgnoredAsync(handle, request, missing, commandTimeout, cancellationToken);
        var presence = await ResolvePresenceAsync(handle, request, missing, commandTimeout, cancellationToken);

        var buckets = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["ignored"] = [],
            ["deleted"] = [],
            ["unchanged"] = [],
            ["unexplained"] = []
        };

        foreach (var path in missing)
        {
            buckets[ClassifyMissing(path, ignored, presence)].Add(path);
        }

        var gap = new AgentHomeWrittenFileGap
        {
            IgnoredCount = buckets["ignored"].Count,
            DeletedCount = buckets["deleted"].Count,
            UnchangedCount = buckets["unchanged"].Count,
            UnexplainedCount = buckets["unexplained"].Count
        };

        await AppendGapEventAsync(request, gap, buckets, cancellationToken);

        if (gap.UnexplainedCount > 0)
        {
            // The run id, never the paths: those are workspace-authored and belong in the run's own log.
            _logger.LogWarning("Patch export for run {RunId} could not account for {Count} file(s) the run wrote; the patch does not carry them.",
                request.RunId,
                gap.UnexplainedCount);
        }

        return gap;
    }

    /// <summary>
    ///     Which of the four reasons explains a written path the diff does not name. A classification the node could
    ///     not obtain leaves every path unexplained rather than guessing a benign reason for it.
    /// </summary>
    private static string ClassifyMissing(string path,
        HashSet<string>? ignored,
        IReadOnlyDictionary<string, char>? presence)
    {
        if (ignored is null || presence is null)
        {
            return "unexplained";
        }

        if (ignored.Contains(path))
        {
            return "ignored";
        }

        if (!presence.TryGetValue(path, out var tag))
        {
            // Neither in the index nor on disk: something removed it between the write and the export.
            return "deleted";
        }

        return tag switch
        {
            'R' => "deleted",

            // ONLY a plain cached entry says the index holds this path unchanged. Every other tag — untracked after
            // `add -A`, skip-worktree, assume-unchanged, modified — the node cannot vouch for.
            'H' => "unchanged",
            _ => "unexplained"
        };
    }

    /// <summary>
    ///     The written paths a <c>.gitignore</c> excludes, or <see langword="null" /> when git could not answer.
    /// </summary>
    /// <remarks>
    ///     <c>check-ignore</c> exits 1 when nothing matched, which is an answer and not a failure; any other non-zero
    ///     exit (a path inside a nested repository makes it exit 128) discards the partial output.
    /// </remarks>
    private async Task<HashSet<string>?> ResolveIgnoredAsync(SandboxHandle handle,
        AgentHomePatchExportRequest request,
        IReadOnlyList<string> missing,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(handle,
            request,
            $"{request.RunId}-patch-check-ignore",
            commandTimeout,
            ["check-ignore", "-z", "--stdin"],
            cancellationToken,
            string.Concat(missing.Select(static path => path + '\0')));

        if (!result.Completed || result.ExitCode is not (0 or 1))
        {
            _logger.LogDebug("Patch export for run {RunId} could not determine which written paths are ignored (exit {ExitCode}).",
                request.RunId,
                result.ExitCode);
            return null;
        }

        return [.. SplitNul(result.StandardOutput)];
    }

    /// <summary>
    ///     The index/working-tree tag git reports per written path, or <see langword="null" /> when it could not
    ///     answer. A path git names in neither role is absent from the map.
    /// </summary>
    /// <remarks>
    ///     <c>-v</c> is load-bearing: a model can mark its own edit <c>assume-unchanged</c> with <c>run_command</c>,
    ///     which makes <c>add -A</c> skip it, and only the lowercase tag <c>-v</c> adds tells that apart from a file
    ///     that genuinely matches the baseline.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, char>?> ResolvePresenceAsync(SandboxHandle handle,
        AgentHomePatchExportRequest request,
        IReadOnlyList<string> missing,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        // :(literal) so a path holding pathspec magic (a leading colon, a glob) matches itself and nothing else.
        string[] arguments =
        [
            "ls-files", "-z", "-t", "-v", "--cached", "--others", "--deleted", "--exclude-standard", "--",
            .. missing.Select(static path => ":(literal)" + path)
        ];

        var result = await RunGitAsync(handle, request, $"{request.RunId}-patch-ls-files", commandTimeout, arguments, cancellationToken);
        if (!IsSuccessful(result))
        {
            _logger.LogDebug("Patch export for run {RunId} could not determine which written paths still exist (exit {ExitCode}).",
                request.RunId,
                result.ExitCode);
            return null;
        }

        var tags = new Dictionary<string, char>(StringComparer.Ordinal);
        foreach (var record in SplitNul(result.StandardOutput))
        {
            if (record.Length < 3 || record[1] != ' ')
            {
                continue;
            }

            // A deleted-from-disk path is also listed as cached; the removal tag is the one that describes it.
            var path = record[2..];
            if (record[0] == 'R' || !tags.ContainsKey(path))
            {
                tags[path] = record[0];
            }
        }

        return tags;
    }

    private async Task AppendGapEventAsync(AgentHomePatchExportRequest request,
        AgentHomeWrittenFileGap gap,
        IReadOnlyDictionary<string, List<string>> buckets,
        CancellationToken cancellationToken)
    {
        var detail = string.Create(CultureInfo.InvariantCulture,
            $"total={gap.Total};ignored={gap.IgnoredCount};deleted={gap.DeletedCount};unchanged={gap.UnchangedCount};unexplained={gap.UnexplainedCount}");

        try
        {
            // The paths ride the run's OWN log and go nowhere else: they are workspace-authored, and the operator
            // reading this run is the only reader that can act on them.
            await request.RunLogger.AppendEventAsync("written_not_exported", detail, buckets, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogDebug(exception, "Patch export for run {RunId} could not append its written-file reconciliation to the run log.", request.RunId);
        }
    }

    /// <summary>Every path the name-status output names, destination and rename source alike.</summary>
    private static HashSet<string> ExportedPaths(string nameStatusOutput)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in ParseNameStatus(nameStatusOutput))
        {
            paths.Add(record.Path);
            if (record.SourcePath is not null)
            {
                paths.Add(record.SourcePath);
            }
        }

        return paths;
    }

    /// <summary>
    ///     Walks the NUL-separated <c>--name-status -z</c> stream positionally: status, path, and for the rename
    ///     and copy statuses a source path ahead of the destination.
    /// </summary>
    /// <remarks>
    ///     The only delimiter is the NUL because that is the point of <c>-z</c>: a path comes back verbatim and may
    ///     hold a double quote, a backslash, a tab or a newline. Splitting on a tab or a newline instead cuts such a
    ///     path in half or leaves git's C-quoting in the name, and the entry then fails its alias split and is
    ///     dropped from <c>changed-files.json</c> — a genuinely changed file missing from the run's review surface.
    /// </remarks>
    private static List<NameStatusRecord> ParseNameStatus(string nameStatusOutput)
    {
        var records = new List<NameStatusRecord>();
        var tokens = nameStatusOutput.Split('\0');
        var index = 0;
        while (index < tokens.Length)
        {
            var status = tokens[index++];
            if (status.Length == 0)
            {
                // The stream is NUL-TERMINATED, so the split always ends on an empty token.
                continue;
            }

            if (index >= tokens.Length)
            {
                // A trailing status with no path names no file; there is nothing to report about it.
                break;
            }

            var path = tokens[index++];
            string? sourcePath = null;
            if (status[0] is 'R' or 'C' && index < tokens.Length)
            {
                // The destination is the changed file; the source is still a path the run touched.
                sourcePath = path;
                path = tokens[index++];
            }

            records.Add(new NameStatusRecord(status, path, sourcePath));
        }

        return records;
    }

    private static IEnumerable<string> SplitNul(string output)
    {
        return output.Split('\0').Where(static entry => entry.Length > 0);
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
        CancellationToken cancellationToken,
        string? standardInput = null)
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
                Timeout = timeout,
                StandardInput = standardInput
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

        return ParseNameStatus(nameStatusOutput)
               .Select(record => TryMapEntry(record, aliasToId))
               .OfType<ChangedFileEntry>()
               .ToList();
    }

    private ChangedFileEntry? TryMapEntry(NameStatusRecord record, IReadOnlyDictionary<string, string> aliasToId)
    {
        var path = record.Path;
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
            ChangeType = MapChangeType(record.Status)
        };
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

    private static AgentHomePatchExport EmptyExport(AgentHomeWrittenFileGap writtenGap)
    {
        return new AgentHomePatchExport
        {
            ChangedFileCount = 0,
            Blocked = false,
            PatchBytes = 0,
            WrittenGap = writtenGap,
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

    /// <summary>
    ///     One <c>--name-status -z</c> record: the status letters, the path the change lands on, and the path a
    ///     rename or copy moved it from.
    /// </summary>
    private readonly record struct NameStatusRecord(string Status, string Path, string? SourcePath);
}
