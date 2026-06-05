namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

/// <summary>
///     Applies exported <c>changes.patch</c> files onto trusted host selected folders. Each sandbox-relative
///     <c>a/&lt;alias&gt;/…</c> / <c>b/&lt;alias&gt;/…</c> prefix is resolved through
///     <see cref="ISelectedFolderResolver" /> and applied only under that host root. The apply path rejects traversal
///     and cross-alias writes, rejects binary changes by default, previews before applying, and logs applied files
///     folder-relative.
/// </summary>
/// <remarks>
///     Security model: all path validation is authoritative and independent of git's behaviour.
///     The written paths are derived from the patch BODY lines (<c>--- a/…</c>, <c>+++ b/…</c>,
///     <c>rename from/to</c>, <c>copy from/to</c>) — NOT the <c>diff --git</c> header — because git acts on the body
///     paths. The header is kept only as a cross-check. The within-root + symlink-escape guard runs over every body
///     path; safety never delegates to git path handling.
///     Residual TOCTOU: there is a bounded symlink-swap window between the <c>--check</c> pass and the actual write.
///     If a caller-controlled symlink is swapped into an intermediate directory after <c>--check</c>, git will reject
///     the write with "beyond a symbolic link" (modern git). This bounded residual risk remains because the host
///     folder is user-trusted and a full transactional fence would require OS-level file locking.
/// </remarks>
internal sealed partial class NodePatchApplyService : INodePatchApplyService
{
    private const string PatchFileName = "changes.patch";
    private const string RunsDirectoryName = "runs";
    private const string PatchesDirectoryName = "patches";
    private const string DefaultRootDirectoryName = "agent-home-state";
    private const string AgentHomeDirectoryName = "agent-home";
    private const string DiffHeaderPrefix = "diff --git ";
    private const string ProviderName = "host-patch-apply";

    private readonly string _contentRootPath;
    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly ILogger<NodePatchApplyService> _logger;
    private readonly AgentHomeOptions _options;
    private readonly ISelectedFolderResolver _resolver;
    private readonly IServiceScopeFactory _scopeFactory;

    public NodePatchApplyService(
        ISelectedFolderResolver resolver,
        IOptions<AgentHomeOptions> options,
        IHostEnvironment hostEnvironment,
        IAgentHomeIdentityProvider identityProvider,
        IServiceScopeFactory scopeFactory,
        ILogger<NodePatchApplyService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options = options.Value;
        _contentRootPath = hostEnvironment.ContentRootPath;
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NodePatchApplyPreview> PreviewAsync(NodePatchApplyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = await BuildPlanAsync(request, cancellationToken).ConfigureAwait(false);
        if (!plan.IsValid)
        {
            return new NodePatchApplyPreview
            {
                CanApply = false,
                Files = plan.Files,
                Rejections = plan.Rejections,
                ContainsBinary = plan.ContainsBinary
            };
        }

        var rejections = new List<string>(plan.Rejections);
        var runner = new HostGitRunner(_options.PatchApplyTimeoutSeconds);
        var numstat = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);
        foreach (var alias in plan.Aliases)
        {
            var check = await CheckSubPatchAsync(runner, alias, cancellationToken).ConfigureAwait(false);
            if (check is null || check.ExitCode != 0)
            {
                rejections.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"alias '{alias.Alias}': patch does not apply cleanly ({Redact(check?.StandardError ?? string.Empty, alias.ResolvedRoot)})"));
                continue;
            }

            var stats = await NumstatSubPatchAsync(runner, alias, cancellationToken).ConfigureAwait(false);
            if (stats is not null && stats.ExitCode == 0)
            {
                MergeNumstat(numstat, alias.Alias, stats.StandardOutput);
            }
        }

        return new NodePatchApplyPreview
        {
            CanApply = rejections.Count == 0,
            Files = ApplyNumstat(plan.Files, numstat),
            Rejections = rejections,
            ContainsBinary = plan.ContainsBinary
        };
    }

    public async Task<NodePatchApplyResult> ApplyApprovedAsync(NodePatchApplyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Re-run the full validation + dry-run check (TOCTOU defense; never blind-apply).
        var plan = await BuildPlanAsync(request, cancellationToken).ConfigureAwait(false);
        var rejections = new List<string>(plan.Rejections);
        var runner = new HostGitRunner(_options.PatchApplyTimeoutSeconds);

        if (plan.IsValid)
        {
            foreach (var alias in plan.Aliases)
            {
                var check = await CheckSubPatchAsync(runner, alias, cancellationToken).ConfigureAwait(false);
                if (check is null || check.ExitCode != 0)
                {
                    rejections.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"alias '{alias.Alias}': patch does not apply cleanly ({Redact(check?.StandardError ?? string.Empty, alias.ResolvedRoot)})"));
                }
            }
        }

        if (!plan.IsValid || rejections.Count != 0)
        {
            await LogRejectionAsync(request.RunId, rejections, cancellationToken).ConfigureAwait(false);
            return new NodePatchApplyResult
            {
                Applied = false,
                AppliedFiles = [],
                Rejections = rejections
            };
        }

        var appliedFiles = new List<PatchApplyFileEntry>();
        var appliedAliases = 0;
        foreach (var alias in plan.Aliases)
        {
            // Residual TOCTOU note: the --check above passed for this alias; the write below runs immediately after.
            // A symlink-swap in an intermediate directory between these two calls is bounded: git rejects "beyond a
            // symbolic link" on modern versions, and the host folder is user-trusted.
            var apply = await ApplySubPatchAsync(runner, alias, cancellationToken).ConfigureAwait(false);
            if (apply is null || apply.ExitCode != 0)
            {
                // A clean --check passed for every alias above, so a non-zero apply here is a rare race. Report the
                // aliases that did land as partially applied rather than silently dropping the failure.
                var partial = appliedAliases > 0;
                rejections.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"alias '{alias.Alias}': apply failed after a clean check ({Redact(apply?.StandardError ?? string.Empty, alias.ResolvedRoot)})"));
                await LogRejectionAsync(request.RunId, rejections, cancellationToken).ConfigureAwait(false);
                return new NodePatchApplyResult
                {
                    Applied = false,
                    AppliedFiles = appliedFiles,
                    Rejections = rejections,
                    PartiallyApplied = partial
                };
            }

            // Populate line counts for the applied files (parity with preview).
            var numstat = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);
            var stats = await NumstatSubPatchAsync(runner, alias, cancellationToken).ConfigureAwait(false);
            if (stats is not null && stats.ExitCode == 0)
            {
                MergeNumstat(numstat, alias.Alias, stats.StandardOutput);
            }

            appliedFiles.AddRange(ApplyNumstat(alias.Files, numstat));
            appliedAliases++;
        }

        await LogAppliedAsync(request.RunId, appliedFiles, cancellationToken).ConfigureAwait(false);

        return new NodePatchApplyResult
        {
            Applied = true,
            AppliedFiles = appliedFiles,
            Rejections = rejections
        };
    }

    private static async Task<HostGitResult?> CheckSubPatchAsync(HostGitRunner runner, AliasPlan alias, CancellationToken cancellationToken)
    {
        return await RunSubPatchAsync(runner, alias, AgentHomeGit.Arguments("apply", "-p2", "--check", "--whitespace=nowarn"), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HostGitResult?> NumstatSubPatchAsync(HostGitRunner runner, AliasPlan alias, CancellationToken cancellationToken)
    {
        return await RunSubPatchAsync(runner, alias, AgentHomeGit.Arguments("apply", "-p2", "--numstat"), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HostGitResult?> ApplySubPatchAsync(HostGitRunner runner, AliasPlan alias, CancellationToken cancellationToken)
    {
        return await RunSubPatchAsync(runner, alias, AgentHomeGit.Arguments("apply", "-p2", "--whitespace=nowarn"), cancellationToken).ConfigureAwait(false);
    }

    private static void MergeNumstat(Dictionary<string, (int Added, int Removed)> numstat, string alias, string output)
    {
        // git apply --numstat lines: "<added>\t<removed>\t<path>" where <path> is the in-patch b-side path that
        // -p2 has already stripped of the a/ + alias prefix, leaving a folder-relative path. Binary file entries
        // emit "-" for both counts. Pure renames with no content changes emit "0\t0\t<path>".
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            var added = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : 0;
            var removed = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0;
            var relative = parts[2].Trim();
            numstat[string.Create(CultureInfo.InvariantCulture, $"{alias}/{relative}")] = (added, removed);
        }
    }

    private static IReadOnlyList<PatchApplyFileEntry> ApplyNumstat(
        IReadOnlyList<PatchApplyFileEntry> files,
        IReadOnlyDictionary<string, (int Added, int Removed)> numstat)
    {
        return files
               .Select(file =>
               {
                   var key = string.Create(CultureInfo.InvariantCulture, $"{file.Alias}/{file.RelativePath}");
                   return numstat.TryGetValue(key, out var stats)
                       ? file with { Added = stats.Added, Removed = stats.Removed }
                       : file;
               })
               .ToArray();
    }

    private static async Task<HostGitResult?> RunSubPatchAsync(HostGitRunner runner, AliasPlan alias, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        // git apply reads the patch from a file path argument (the sub-patch is written to a temp file under the system
        // temp dir, never under a selected folder). Append the temp path as the final argument.
        var tempPatch = Path.Combine(Path.GetTempPath(), "agenthome-apply-" + Guid.NewGuid().ToString("N") + ".patch");
        try
        {
            await File.WriteAllTextAsync(tempPatch, alias.SubPatch, cancellationToken).ConfigureAwait(false);
            var fullArguments = arguments.Append(tempPatch).ToArray();
            return await runner.RunAsync(alias.ResolvedRoot, fullArguments, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            TryDeleteFile(tempPatch);
        }
    }

    private async Task<ApplyPlan> BuildPlanAsync(NodePatchApplyRequest request, CancellationToken cancellationToken)
    {
        // Validate the untrusted RunId shape before composing any path.
        if (!IsValidRunId(request.RunId))
        {
            return ApplyPlan.Invalid("the run id is not a valid identifier.");
        }

        // Resolve the patch path and gate on changes.patch presence (never changed-files.json).
        var patchPath = Path.Combine(ResolveAgentHomeRoot(), RunsDirectoryName, request.RunId, PatchesDirectoryName, PatchFileName);
        var fileInfo = new FileInfo(patchPath);
        if (!fileInfo.Exists)
        {
            return ApplyPlan.Invalid("no exported patch is available for this run.");
        }

        if (fileInfo.Length == 0)
        {
            return ApplyPlan.Invalid("the exported patch is empty.");
        }

        if (fileInfo.Length > _options.MaxPatchBytes)
        {
            return ApplyPlan.Invalid("the exported patch exceeds the maximum allowed size.");
        }

        string patchText;
        try
        {
            patchText = await File.ReadAllTextAsync(patchPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return ApplyPlan.Invalid("the exported patch could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            return ApplyPlan.Invalid("the exported patch could not be read.");
        }

        var blocks = SplitBlocks(patchText);
        if (blocks.Count == 0)
        {
            return ApplyPlan.Invalid("the exported patch contains no file changes.");
        }

        var rejections = new List<string>();
        var containsBinary = false;
        var parsed = new List<ParsedBlock>();
        foreach (var block in blocks)
        {
            var parseResult = ParseBlock(block);
            if (parseResult.Rejection is not null)
            {
                rejections.Add(parseResult.Rejection);
                continue;
            }

            if (parseResult.IsBinary)
            {
                containsBinary = true;
            }

            parsed.Add(parseResult);
        }

        // Binary reject by default — reject the whole apply when a binary block is present and the option is off.
        if (containsBinary && !_options.AllowBinaryPatchApply)
        {
            rejections.Add("the patch contains a binary change, which is not allowed.");
        }

        if (rejections.Count != 0)
        {
            return ApplyPlan.WithRejections(rejections, containsBinary);
        }

        var aliasPlans = new List<AliasPlan>();
        var files = new List<PatchApplyFileEntry>();
        foreach (var group in parsed.GroupBy(item => item.Alias, StringComparer.Ordinal))
        {
            var aliasPlan = await BuildAliasPlanAsync(group.Key, [.. group], rejections, cancellationToken).ConfigureAwait(false);
            if (aliasPlan is null)
            {
                continue;
            }

            aliasPlans.Add(aliasPlan);
            files.AddRange(aliasPlan.Files);
        }

        if (rejections.Count != 0)
        {
            return ApplyPlan.WithRejections(rejections, containsBinary);
        }

        return new ApplyPlan
        {
            IsValid = true,
            Aliases = aliasPlans,
            Files = files,
            Rejections = rejections,
            ContainsBinary = containsBinary
        };
    }

    private async Task<AliasPlan?> BuildAliasPlanAsync(string alias, IReadOnlyList<ParsedBlock> blocks, List<string> rejections, CancellationToken cancellationToken)
    {
        // Map alias -> id -> trusted host path. Unknown/unresolvable alias rejects (fail closed).
        ResolvedSelectedFolder resolved;
        try
        {
            var references = await _resolver.ListReferencesAsync(cancellationToken).ConfigureAwait(false);
            var reference = references.FirstOrDefault(candidate => string.Equals(candidate.Alias, alias, StringComparison.Ordinal));
            if (reference is null)
            {
                rejections.Add(string.Create(CultureInfo.InvariantCulture, $"alias '{alias}': not a registered selected folder."));
                return null;
            }

            resolved = await _resolver.ResolveAsync(reference.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (SelectedFolderValidationException)
        {
            rejections.Add(string.Create(CultureInfo.InvariantCulture, $"alias '{alias}': not a registered selected folder."));
            return null;
        }

        var resolvedRoot = HostPathSafety.TryResolveTrustedRoot(resolved.HostPath);
        if (resolvedRoot is null)
        {
            rejections.Add(string.Create(CultureInfo.InvariantCulture, $"alias '{alias}': its host root could not be resolved."));
            return null;
        }

        // Every target relative path (extracted from the BODY lines that git actually acts on) must resolve
        // under the alias root. This is our own authoritative guard — we never rely on git's path validation.
        // A symlinked intermediate dir that escapes the root is also rejected (EscapesViaReparsePoint).
        foreach (var block in blocks)
        {
            foreach (var relativePath in block.TargetRelativePaths)
            {
                var candidate = Path.GetFullPath(Path.Combine(resolvedRoot, relativePath));
                if (!HostPathSafety.IsPathWithinRoot(resolvedRoot, candidate))
                {
                    rejections.Add(string.Create(CultureInfo.InvariantCulture, $"alias '{alias}': a target path escapes the folder root."));
                    return null;
                }

                if (EscapesViaReparsePoint(resolvedRoot, candidate))
                {
                    rejections.Add(string.Create(CultureInfo.InvariantCulture, $"alias '{alias}': a target path traverses a symlink that escapes the folder root."));
                    return null;
                }
            }
        }

        var subPatch = string.Concat(blocks.Select(block => block.Text));
        var files = blocks.SelectMany(block => block.Files).ToArray();
        return new AliasPlan(alias, resolvedRoot, subPatch, files);
    }

    private static bool EscapesViaReparsePoint(string resolvedRoot, string candidate)
    {
        // Walk from the candidate's nearest existing ancestor up to the root; a reparse point that resolves outside the
        // root is an escape. The root itself was already canonicalized (symlinks followed) by TryResolveTrustedRoot.
        var current = new DirectoryInfo(Path.GetDirectoryName(candidate) ?? resolvedRoot);
        while (current is not null && current.Exists && HostPathSafety.IsPathWithinRoot(resolvedRoot, Path.TrimEndingDirectorySeparator(current.FullName)))
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), resolvedRoot, StringComparison.Ordinal))
            {
                return false;
            }

            if (HostPathSafety.IsReparsePoint(current)
                && (!HostPathSafety.TryResolveReparseWithinRoot(current, resolvedRoot, out var withinRoot) || !withinRoot))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool IsValidRunId(string runId)
    {
        return !string.IsNullOrEmpty(runId) && RunIdRegex().IsMatch(runId);
    }

    private string ResolveAgentHomeRoot()
    {
        var baseRoot = string.IsNullOrWhiteSpace(_options.RootPath)
            ? Path.Combine(_contentRootPath, DefaultRootDirectoryName)
            : _options.RootPath;
        return Path.Combine(baseRoot, AgentHomeDirectoryName);
    }

    private static string Redact(string text, string resolvedRoot)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "git rejected the patch.";
        }

        // Redact the temporary-directory prefix first (handles spaces in Path.GetTempPath()), then the resolved root,
        // then collapse the residual agenthome-apply filename.
        var redacted = text.Replace(Path.GetTempPath(), "<tmp>/", StringComparison.Ordinal);
        redacted = redacted.Replace(resolvedRoot, "<folder>", StringComparison.Ordinal);
        redacted = TempPatchFilenameRegex().Replace(redacted, "<patch>");
        return redacted.Trim();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial Regex RunIdRegex();

    // Matches only the residual filename after the temporary-directory prefix has already been replaced.
    [GeneratedRegex(@"agenthome-apply-[0-9a-fA-F]{32}\.patch")]
    private static partial Regex TempPatchFilenameRegex();

    private sealed record AliasPlan(string Alias, string ResolvedRoot, string SubPatch, IReadOnlyList<PatchApplyFileEntry> Files);

    private sealed record ApplyPlan
    {
        public bool IsValid { get; init; }

        public IReadOnlyList<AliasPlan> Aliases { get; init; } = [];

        public IReadOnlyList<PatchApplyFileEntry> Files { get; init; } = [];

        public IReadOnlyList<string> Rejections { get; init; } = [];

        public bool ContainsBinary { get; init; }

        public static ApplyPlan Invalid(string reason)
        {
            return new ApplyPlan { IsValid = false, Rejections = [reason] };
        }

        public static ApplyPlan WithRejections(IReadOnlyList<string> rejections, bool containsBinary)
        {
            return new ApplyPlan { IsValid = false, Rejections = rejections, ContainsBinary = containsBinary };
        }
    }
}
