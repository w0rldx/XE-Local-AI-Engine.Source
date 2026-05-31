namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

/// <summary>
///     host patch apply host patch apply. Applies exported <c>changes.patch</c> onto the real
///     host selected folders, mapping each sandbox-relative <c>a/&lt;alias&gt;/…</c> / <c>b/&lt;alias&gt;/…</c> prefix
///     back to its trusted host root via <see cref="ISelectedFolderResolver" /> and applying only under that root.
///     Traversal-rejected, cross-alias-rejected, binary-rejected by default, preview-before-apply, every applied file
///     logged folder-relative.
/// </summary>
/// <remarks>
///     Security model (§9.2 / §11 / §17): all path validation is authoritative and independent of git's behaviour.
///     The written paths are derived from the patch BODY lines (<c>--- a/…</c>, <c>+++ b/…</c>,
///     <c>rename from/to</c>, <c>copy from/to</c>) — NOT the <c>diff --git</c> header — because git acts on the body
///     paths. The header is kept only as a cross-check. The within-root + symlink-escape guard runs over every body
///     path; the plan explicitly forbids delegating safety to git ("never trust git's path handling", plan §D3/§D4).
///     Residual TOCTOU: there is a bounded symlink-swap window between the <c>--check</c> pass and the actual write.
///     If a caller-controlled symlink is swapped into an intermediate directory after <c>--check</c>, git will reject
///     the write with "beyond a symbolic link" (modern git). This is documented-bounded per plan §281 — the host
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
        // D2 step 1: validate the untrusted RunId shape before composing any path.
        if (!IsValidRunId(request.RunId))
        {
            return ApplyPlan.Invalid("the run id is not a valid identifier.");
        }

        // D2 step 2/3: resolve the patch path and gate on changes.patch presence (never changed-files.json).
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

        // D5: binary reject by default — reject the whole apply when a binary block is present and the option is off.
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

        // D3 step 4: every target relative path (extracted from the BODY lines that git actually acts on) must resolve
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

    private static List<string> SplitBlocks(string patchText)
    {
        var blocks = new List<string>();
        var lines = patchText.Split('\n');
        var builder = new StringBuilder();
        var inBlock = false;

        foreach (var line in lines)
        {
            if (line.StartsWith(DiffHeaderPrefix, StringComparison.Ordinal))
            {
                if (inBlock)
                {
                    blocks.Add(builder.ToString());
                    builder.Clear();
                }

                inBlock = true;
            }

            if (inBlock)
            {
                builder.Append(line).Append('\n');
            }
        }

        if (inBlock && builder.Length > 0)
        {
            blocks.Add(builder.ToString());
        }

        return blocks;
    }

    /// <summary>
    ///     Parses a single per-file patch block. Security contract: the written paths are derived from the BODY lines
    ///     (<c>--- a/…</c>, <c>+++ b/…</c>, <c>rename from/to</c>, <c>copy from/to</c>) — the paths git actually acts
    ///     on — NOT the <c>diff --git</c> header. The header is used only as a cross-check (b-path must match <c>+++ b/</c>).
    ///     This makes all alias / traversal / cross-alias guards authoritative independent of git.
    /// </summary>
    private static ParsedBlock ParseBlock(string block)
    {
        var lines = block.Split('\n');

        var isBinary = block.Contains("GIT binary patch", StringComparison.Ordinal)
                       || lines.Any(line => line.StartsWith("Binary files ", StringComparison.Ordinal));

        // Step 1: extract the authoritative paths from the body lines.
        // Every path git can act on comes from one of these line prefixes. /dev/null is skipped (new/deleted).
        // The prefix strings are kept as constants so S125 ("commented-out code") is not triggered by raw
        // unified-diff sigils appearing inline.
        const string prefixSource = "---";
        const string prefixDest = "+++";
        var bodyPaths = new List<(string Prefix, string Raw)>();
        foreach (var line in lines)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal)
                && !line.StartsWith("--- /dev/null", StringComparison.Ordinal))
            {
                bodyPaths.Add((prefixSource, line[4..]));
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal)
                     && !line.StartsWith("+++ /dev/null", StringComparison.Ordinal))
            {
                bodyPaths.Add((prefixDest, line[4..]));
            }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                bodyPaths.Add(("rename from", line[12..]));
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                bodyPaths.Add(("rename to", line[10..]));
            }
            else if (line.StartsWith("copy from ", StringComparison.Ordinal))
            {
                bodyPaths.Add(("copy from", line[10..]));
            }
            else if (line.StartsWith("copy to ", StringComparison.Ordinal))
            {
                bodyPaths.Add(("copy to", line[8..]));
            }
        }

        if (bodyPaths.Count == 0)
        {
            // A mode-only block (e.g. old mode 100644 / new mode 100755) has no unified-diff body lines. Git acts
            // on the path derived from the header, so we must validate that header path through the same guards as
            // body paths (ContainsTraversal + SplitAlias) rather than leaving git as the only backstop.
            var headerPath = TryParseHeaderAPath(lines[0]);
            if (headerPath is null)
            {
                return ParsedBlock.Rejected("a mode-only patch block has an unparseable or missing header path.");
            }

            var (headerAlias, headerRelative) = headerPath.Value;
            if (ContainsTraversal(headerRelative))
            {
                return ParsedBlock.Rejected("a patch block targets a path outside its folder.");
            }

            return new ParsedBlock
            {
                Alias = headerAlias,
                Text = block,
                IsBinary = isBinary,
                TargetRelativePaths = [headerRelative],
                Files = []
            };
        }

        // Step 2: split each body path into alias + relative; validate traversal and cross-alias.
        var allAliasResults = new List<(string Prefix, string Alias, string Relative)>();
        foreach (var (prefix, raw) in bodyPaths)
        {
            // Unified-diff body paths carry an "a/" or "b/" diff prefix; rename/copy lines do not.
            var normalized = raw.Trim();
            if (normalized.StartsWith("a/", StringComparison.Ordinal) || normalized.StartsWith("b/", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }

            var split = SplitAlias(normalized);
            if (split is null)
            {
                return ParsedBlock.Rejected("a patch block has a path with no alias segment.");
            }

            var (alias, relative) = split.Value;

            if (ContainsTraversal(relative))
            {
                return ParsedBlock.Rejected("a patch block targets a path outside its folder.");
            }

            allAliasResults.Add((prefix, alias, relative));
        }

        // All paths in the block must belong to the same alias (cross-alias rename/copy is a path-escape vector).
        var aliases = allAliasResults.Select(result => result.Alias).Distinct(StringComparer.Ordinal).ToArray();
        if (aliases.Length != 1)
        {
            return ParsedBlock.Rejected("a patch block renames or copies across selected folders.");
        }

        var blockAlias = aliases[0];

        // Step 3: cross-check header b-path alias against the authoritative body alias.
        // The header can mis-split on paths whose name contains a space followed by a single letter and slash
        // (e.g. "dir b/file"), so it is not used as the authoritative source. When a body plus-plus path is
        // present the header alias should agree; a mismatch is a crafted-patch signal and is rejected.
        var destBodyPath = allAliasResults.FirstOrDefault(result => result.Prefix == prefixDest);
        if (destBodyPath != default)
        {
            var headerBAlias = ExtractAliasFromHeader(lines[0]);
            if (headerBAlias is not null && !string.Equals(headerBAlias, blockAlias, StringComparison.Ordinal))
            {
                return ParsedBlock.Rejected("the patch header b-path does not match the body destination path.");
            }
        }

        // Step 4: collect all distinct relative target paths for the within-root guard in BuildAliasPlanAsync.
        var targetPaths = allAliasResults.Select(result => result.Relative).Distinct(StringComparer.Ordinal).ToArray();

        var changeType = DetermineChangeType(block);

        // Display path: destination side, or the source side for a pure delete (no destination body line).
        var bRelative = allAliasResults
                        .Where(result => result.Prefix == prefixDest || result.Prefix is "rename to" or "copy to")
                        .Select(result => result.Relative)
                        .FirstOrDefault();
        var aRelative = allAliasResults
                        .Where(result => result.Prefix == prefixSource || result.Prefix is "rename from" or "copy from")
                        .Select(result => result.Relative)
                        .FirstOrDefault();
        var displayRelative = changeType == "deleted"
            ? (aRelative ?? targetPaths[0])
            : (bRelative ?? targetPaths[0]);

        var files = new List<PatchApplyFileEntry>
        {
            new()
            {
                Alias = blockAlias,
                RelativePath = displayRelative,
                ChangeType = changeType
            }
        };

        return new ParsedBlock
        {
            Alias = blockAlias,
            Text = block,
            IsBinary = isBinary,
            TargetRelativePaths = targetPaths,
            Files = files
        };
    }

    /// <summary>
    ///     Extracts the alias component from the <c>diff --git a/…</c> header for the cross-check only.
    ///     Returns <see langword="null" /> when the header cannot be parsed (non-fatal — the guard is advisory).
    /// </summary>
    private static string? ExtractAliasFromHeader(string header)
    {
        if (!header.StartsWith(DiffHeaderPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        // The header is "diff --git a/<rest> b/<rest>". We only need the alias from the a/ side, which is the first
        // path component after "a/". A mis-split here is safe because this is advisory only — advisory-path guard guards.
        var afterPrefix = header[DiffHeaderPrefix.Length..];
        if (!afterPrefix.StartsWith("a/", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = afterPrefix[2..];
        var slashIndex = rest.IndexOf('/', StringComparison.Ordinal);
        return slashIndex > 0 ? rest[..slashIndex] : null;
    }

    /// <summary>
    ///     Parses the <c>a/…</c> path from a <c>diff --git a/… b/…</c> header into <c>(alias, relative)</c> using
    ///     <see cref="SplitAlias" />. Used for mode-only blocks that carry no <c>---</c>/<c>+++</c> body lines; the
    ///     result is fed through the same traversal and within-root guards as all other target paths.
    ///     Returns <see langword="null" /> when the header cannot be parsed.
    /// </summary>
    private static (string Alias, string Relative)? TryParseHeaderAPath(string header)
    {
        if (!header.StartsWith(DiffHeaderPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var afterPrefix = header[DiffHeaderPrefix.Length..];
        if (!afterPrefix.StartsWith("a/", StringComparison.Ordinal))
        {
            return null;
        }

        // Strip the "a/" prefix and find the end of the a-side: the header is "a/<apath> b/<bpath>" and the a-path
        // ends at the first " b/" that is followed by the same path (for symmetric headers). For the traversal guard
        // we only need the a-side; SplitAlias handles alias extraction from the a-prefix-stripped path.
        var aRest = afterPrefix[2..];

        // Locate the " b/" separator by scanning from the end — in symmetric headers the b-path mirrors the a-path
        // so the separator is at position (len(aRest) - len(bpath) - 3). As a safe fallback: take everything up to
        // the first occurrence of " b/" which is the canonical separator for well-formed headers.
        var sepIndex = aRest.IndexOf(" b/", StringComparison.Ordinal);
        var aPath = sepIndex > 0 ? aRest[..sepIndex] : aRest;

        return SplitAlias(aPath);
    }

    private static string DetermineChangeType(string block)
    {
        if (block.Contains("\nrename from ", StringComparison.Ordinal) || block.Contains("\nrename to ", StringComparison.Ordinal))
        {
            return "renamed";
        }

        if (block.Contains("\ncopy from ", StringComparison.Ordinal) || block.Contains("\ncopy to ", StringComparison.Ordinal))
        {
            return "copied";
        }

        if (block.Contains("\nnew file mode ", StringComparison.Ordinal))
        {
            return "added";
        }

        if (block.Contains("\ndeleted file mode ", StringComparison.Ordinal))
        {
            return "deleted";
        }

        return "modified";
    }

    private static (string Alias, string Relative)? SplitAlias(string path)
    {
        // Path arrives with the a/ or b/ diff prefix already stripped. Split on the first '/' into alias + relative.
        var normalized = path.Replace('\\', '/');
        var separatorIndex = normalized.IndexOf('/', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == normalized.Length - 1)
        {
            return null;
        }

        var alias = normalized[..separatorIndex];
        var relative = normalized[(separatorIndex + 1)..];
        return relative.Length == 0 ? null : (alias, relative);
    }

    private static bool ContainsTraversal(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/');
        return segments.Any(segment => segment is "..");
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

    private async Task LogAppliedAsync(string runId, IReadOnlyList<PatchApplyFileEntry> files, CancellationToken cancellationToken)
    {
        var detail = string.Join(';', files.Select(file => string.Create(CultureInfo.InvariantCulture, $"{file.Alias}/{file.RelativePath}")));
        await AppendEventSafelyAsync(runId, "patch_applied", detail, cancellationToken).ConfigureAwait(false);
    }

    private async Task LogRejectionAsync(string runId, IReadOnlyList<string> rejections, CancellationToken cancellationToken)
    {
        await AppendEventSafelyAsync(runId, "patch_apply_rejected", string.Join(';', rejections), cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendEventSafelyAsync(string runId, string eventName, string? detail, CancellationToken cancellationToken)
    {
        // observability guard: Best-effort logging — broadened to catch ANY exception from identity/logger so a failed log can
        // never surface after a successful host mutation. OperationCanceledException from the caller's token is NOT
        // caught here; it will propagate only from the caller's own await, not from this helper.
        try
        {
            var logDirectory = Path.Combine(ResolveAgentHomeRoot(), RunsDirectoryName, runId, "logs");
            if (!Directory.Exists(logDirectory))
            {
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var runLogger = scope.ServiceProvider.GetRequiredService<IAgentHomeRunLogger>();
            var identity = await _identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
            await runLogger.OpenAsync(
                new AgentHomeRunLogContext
                {
                    RunId = runId,
                    HostLogDirectory = logDirectory,
                    NodeId = identity.NodeId,
                    OwnerUserId = identity.OwnerUserId,
                    ProviderName = ProviderName
                },
                cancellationToken).ConfigureAwait(false);
            await runLogger.AppendEventAsync(eventName, detail, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller-cancel: do not swallow — propagate so the caller knows the operation was cancelled.
            throw;
        }
        catch (Exception exception)
        {
            // Any other failure (identity error, I/O, DI, logger) is swallowed. A log write must never throw past
            // a successful host mutation (D6 / plan §9.2 best-effort contract).
            _logger.LogDebug(exception, "AgentHome patch apply log append for {EventName} failed.", eventName);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial Regex RunIdRegex();

    // Matches only the residual filename after the temporary-directory prefix has already been replaced.
    [GeneratedRegex(@"agenthome-apply-[0-9a-fA-F]{32}\.patch")]
    private static partial Regex TempPatchFilenameRegex();

    private sealed record AliasPlan(string Alias, string ResolvedRoot, string SubPatch, IReadOnlyList<PatchApplyFileEntry> Files);

    private sealed record ParsedBlock
    {
        public string Alias { get; init; } = string.Empty;

        public string Text { get; init; } = string.Empty;

        public bool IsBinary { get; init; }

        public IReadOnlyList<string> TargetRelativePaths { get; init; } = [];

        public IReadOnlyList<PatchApplyFileEntry> Files { get; init; } = [];

        public string? Rejection { get; init; }

        public static ParsedBlock Rejected(string reason)
        {
            return new ParsedBlock { Rejection = reason };
        }
    }

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
