namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Patch export implementation for <see cref="IAgentHomePatchService" />. Runs two in-sandbox <c>git diff</c>
///     commands against the workspace-copy baseline (a full <c>--binary</c> patch and a <c>--name-status</c> summary)
///     with the byte-stabilizing git flags,
///     captures their standard output (the sandbox SPI is shell-neutral, so the worker — not a shell redirection — owns
///     the file write), then writes <c>changes.patch</c> and <c>changed-files.json</c> under the host-side
///     <c>runs/&lt;run-id&gt;/patches/</c> directory. A patch over <see cref="AgentHomeOptions.MaxPatchBytes" /> is
///     blocked: the metadata file is still written, the oversized patch is not. All model-facing paths are run-relative
///     — never a host path.
/// </summary>
internal sealed class AgentHomePatchService : IAgentHomePatchService
{
    private static readonly JsonSerializerOptions ChangedFilesJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILogger<AgentHomePatchService> _logger;
    private readonly AgentHomeOptions _options;
    private readonly ISandboxRuntimeProvider _provider;

    public AgentHomePatchService(
        ISandboxRuntimeProvider provider,
        IOptions<AgentHomeOptions> options,
        ILogger<AgentHomePatchService> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AgentHomePatchExport> ExportPatchAsync(
        SandboxHandle handle,
        AgentHomePatchExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var commandTimeout = TimeSpan.FromSeconds(_options.CommandTimeoutSeconds);

        // Full binary-aware patch. Captured from standard output; the worker writes the file, since the SPI
        // carries no shell redirection.
        var patchResult = await _provider.ExecuteAsync(
            handle,
            DiffCommand(
                $"{request.RunId}-patch-diff",
                commandTimeout,
                "diff", "--binary", "--find-renames=50%", "--find-copies=50%", "--src-prefix=a/", "--dst-prefix=b/", "HEAD", "--", "."),
            cancellationToken).ConfigureAwait(false);

        // Name-status summary used to build changed-files.json.
        var statusResult = await _provider.ExecuteAsync(
            handle,
            DiffCommand(
                $"{request.RunId}-patch-status",
                commandTimeout,
                "diff", "--name-status", "--find-renames=50%", "--find-copies=50%", "HEAD", "--", "."),
            cancellationToken).ConfigureAwait(false);

        if (!IsSuccessful(patchResult) || !IsSuccessful(statusResult))
        {
            // A non-zero exit or an incomplete command means no patch could be produced. Surface that distinctly so it
            // is not reported as a clean zero-change run; write no artifacts. (Real non-zero git exits are exercised by
            // the local-container provider in local-container sandbox — the fake's git is scripted.)
            _logger.LogWarning(
                "Patch export for run {RunId} aborted: patch diff exit {PatchExit} (completed {PatchCompleted}), name-status exit {StatusExit} (completed {StatusCompleted}).",
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
        await File.WriteAllTextAsync(Path.Combine(patchesDirectory, "changed-files.json"), changedFilesJson, cancellationToken)
            .ConfigureAwait(false);

        var patchText = patchResult.StandardOutput;
        var patchBytes = Encoding.UTF8.GetByteCount(patchText);
        if (patchBytes > _options.MaxPatchBytes)
        {
            // Over budget: keep the changed-file metadata, drop the oversized patch.
            _logger.LogWarning(
                "Patch for run {RunId} is {Bytes} byte(s), over the {Budget}-byte budget; changes.patch not written.",
                request.RunId,
                patchBytes,
                _options.MaxPatchBytes);

            return new AgentHomePatchExport
            {
                ChangedFileCount = changedFiles.Count,
                Blocked = true,
                PatchBytes = patchBytes,
                PatchRelativePath = null,
                ChangedFilesRelativePath = changedFilesRelative
            };
        }

        await File.WriteAllTextAsync(Path.Combine(patchesDirectory, "changes.patch"), patchText, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Exported patch for run {RunId}: {ChangedCount} changed file(s), {Bytes} byte(s).",
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

    private static SandboxCommandRequest DiffCommand(string executionId, TimeSpan timeout, params string[] tail)
    {
        return new SandboxCommandRequest
        {
            ExecutionId = executionId,
            Executable = AgentHomeGit.Executable,
            Arguments = AgentHomeGit.Arguments(tail),
            WorkingDirectory = AgentHomeGit.WorkspaceSelectedRoot,
            Timeout = timeout
        };
    }

    private static bool IsSuccessful(SandboxCommandResult result)
    {
        return result.Completed && result.ExitCode == 0;
    }

    private List<ChangedFileEntry> ParseChangedFiles(
        string nameStatusOutput,
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

        var separator = path.IndexOf('/', StringComparison.Ordinal);
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
