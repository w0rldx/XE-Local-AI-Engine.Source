namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Applies exported <c>changes.patch</c> files onto trusted host selected folders, resolving each sandbox-relative
///     <c>a/&lt;alias&gt;/…</c> or <c>b/&lt;alias&gt;/…</c> prefix through <see cref="ISelectedFolderResolver" />.
/// </summary>
/// <remarks>
///     The apply happens only under that host root, rejects traversal and cross-alias writes, rejects binary changes by default,
///     previews before applying and logs applied files folder-relative. All path validation is authoritative and independent of git:
///     the written paths come from the patch BODY lines (<c>--- a/…</c>, <c>+++ b/…</c>, <c>rename from/to</c>, <c>copy from/to</c>)
///     rather than the <c>diff --git</c> header, because git acts on the body paths and the header is a cross-check only. Residual
///     TOCTOU: a bounded symlink-swap window between <c>--check</c> and the write, which git rejects and the trusted host folder bounds.
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

    /// <summary>
    ///     Node-wide single-flight around the mutating half of <see cref="ApplyApprovedAsync" />. Static because the
    ///     service is scoped: a per-instance gate is a gate per request, which is no gate. Applies are not
    ///     transactional against each other, so they serialize.
    /// </summary>
    private static readonly SemaphoreSlim ApplyGate = new(initialCount: 1, maxCount: 1);

    private readonly string _dataDirectoryRoot;
    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly ILogger<NodePatchApplyService> _logger;
    private readonly AgentHomeOptions _options;
    private readonly ISelectedFolderResolver _resolver;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IServiceScopeFactory _scopeFactory;

    public NodePatchApplyService(ISelectedFolderResolver resolver,
        IOptions<AgentHomeOptions> options,
        INodeRuntimeSettings runtimeSettings,
        INodeDataDirectory dataDirectory,
        IAgentHomeIdentityProvider identityProvider,
        IServiceScopeFactory scopeFactory,
        ILogger<NodePatchApplyService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options = options.Value;
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _dataDirectoryRoot = dataDirectory.Root;
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NodePatchApplyPreview> PreviewAsync(NodePatchApplyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = await BuildPlanAsync(request, cancellationToken);
        if (!plan.IsValid)
        {
            return new NodePatchApplyPreview
            {
                CanApply = false,
                Files = plan.Files,
                Rejections = plan.Rejections,
                ContainsBinary = plan.ContainsBinary,
                PatchSha256 = plan.PatchSha256,
                PatchMissing = plan.PatchMissing
            };
        }

        var rejections = new List<string>(plan.Rejections);
        var runner = new HostGitRunner(_options.PatchApplyTimeoutSeconds);
        var numstat = new Dictionary<string, LineStat>(StringComparer.Ordinal);
        foreach (var alias in plan.Aliases)
        {
            var check = await CheckSubPatchAsync(runner, alias, cancellationToken);
            if (check is null || check.ExitCode != 0)
            {
                rejections.Add(string.Create(CultureInfo.InvariantCulture,
                    $"alias '{alias.Alias}': patch does not apply cleanly ({Redact(check?.StandardError ?? string.Empty, alias.ResolvedRoot)})"));
                continue;
            }

            var stats = await NumstatSubPatchAsync(runner, alias, cancellationToken);
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
            ContainsBinary = plan.ContainsBinary,
            PatchSha256 = plan.PatchSha256
        };
    }

    public async Task<NodePatchApplyResult> ApplyApprovedAsync(NodePatchApplyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Everything below is held under the node-wide gate, re-validation included: the --check that clears an apply
        // is only worth anything if no other apply writes into the same folder between it and the write it cleared.
        await ApplyGate.WaitAsync(cancellationToken);
        try
        {
            return await ApplyApprovedCoreAsync(request, cancellationToken);
        }
        finally
        {
            _ = ApplyGate.Release();
        }
    }

    private async Task<NodePatchApplyResult> ApplyApprovedCoreAsync(NodePatchApplyRequest request, CancellationToken cancellationToken)
    {
        // Re-run the full validation + dry-run check (TOCTOU defense; never blind-apply).
        var plan = await BuildPlanAsync(request, cancellationToken);
        var rejections = new List<string>(plan.Rejections);
        var runner = new HostGitRunner(_options.PatchApplyTimeoutSeconds);

        if (plan.IsValid)
        {
            foreach (var alias in plan.Aliases)
            {
                var check = await CheckSubPatchAsync(runner, alias, cancellationToken);
                if (check is null || check.ExitCode != 0)
                {
                    rejections.Add(string.Create(CultureInfo.InvariantCulture,
                        $"alias '{alias.Alias}': patch does not apply cleanly ({Redact(check?.StandardError ?? string.Empty, alias.ResolvedRoot)})"));
                }
            }
        }

        if (!plan.IsValid || rejections.Count != 0)
        {
            await LogRejectionAsync(request.RunId, rejections, cancellationToken);
            return new NodePatchApplyResult
            {
                Applied = false,
                AppliedFiles = [],
                Rejections = rejections,
                PatchMissing = plan.PatchMissing
            };
        }

        var appliedFiles = new List<PatchApplyFileEntry>();
        var appliedAliases = 0;
        foreach (var alias in plan.Aliases)
        {
            // Residual TOCTOU: --check passed for this alias and the write runs immediately after. A symlink swap in an
            // intermediate directory between the two is bounded — git rejects it, and the host folder is user-trusted.
            HostGitResult? apply;
            try
            {
                apply = await ApplySubPatchAsync(runner, alias, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // git is not transactional across files, so a cancelled apply can leave this alias half written.
                // Logged on an uncancelled token, or the run log keeps no record that the apply ever started.
                var cancelled = new List<string>(rejections)
                {
                    string.Create(CultureInfo.InvariantCulture,
                        $"alias '{alias.Alias}': the apply was cancelled while running; this folder may be partly written.")
                };
                await LogRejectionAsync(request.RunId, cancelled, CancellationToken.None);
                throw;
            }


            if (apply is null || apply.ExitCode != 0)
            {
                // A clean --check passed for every alias above, so a non-zero apply here is a rare race. Report the
                // aliases that did land as partially applied rather than silently dropping the failure.
                var partial = appliedAliases > 0;
                rejections.Add(string.Create(CultureInfo.InvariantCulture,
                    $"alias '{alias.Alias}': apply failed after a clean check ({Redact(apply?.StandardError ?? string.Empty, alias.ResolvedRoot)})"));
                await LogRejectionAsync(request.RunId, rejections, cancellationToken);
                return new NodePatchApplyResult
                {
                    Applied = false,
                    AppliedFiles = appliedFiles,
                    Rejections = rejections,
                    PartiallyApplied = partial
                };
            }

            // Populate line counts for the applied files (parity with preview).
            var numstat = new Dictionary<string, LineStat>(StringComparer.Ordinal);
            var stats = await NumstatSubPatchAsync(runner, alias, cancellationToken);
            if (stats is not null && stats.ExitCode == 0)
            {
                MergeNumstat(numstat, alias.Alias, stats.StandardOutput);
            }

            appliedFiles.AddRange(ApplyNumstat(alias.Files, numstat));
            appliedAliases++;
        }

        await LogAppliedAsync(request.RunId, appliedFiles, cancellationToken);

        return new NodePatchApplyResult
        {
            Applied = true,
            AppliedFiles = appliedFiles,
            Rejections = rejections
        };
    }

    private static async Task<HostGitResult?> CheckSubPatchAsync(HostGitRunner runner, AliasPlan alias, CancellationToken cancellationToken)
    {
        return await RunSubPatchAsync(runner, alias, AgentHomeGit.Arguments("apply", "-p2", "--check", "--whitespace=nowarn"), cancellationToken);
    }

    private static async Task<HostGitResult?> NumstatSubPatchAsync(HostGitRunner runner, AliasPlan alias, CancellationToken cancellationToken)
    {
        return await RunSubPatchAsync(runner, alias, AgentHomeGit.Arguments("apply", "-p2", "--numstat"), cancellationToken);
    }

    private static async Task<HostGitResult?> ApplySubPatchAsync(HostGitRunner runner, AliasPlan alias, CancellationToken cancellationToken)
    {
        return await RunSubPatchAsync(runner, alias, AgentHomeGit.Arguments("apply", "-p2", "--whitespace=nowarn"), cancellationToken);
    }

    private static void MergeNumstat(Dictionary<string, LineStat> numstat, string alias, string output)
    {
        // git apply --numstat lines carry added, removed and the b-side path that -p2 already stripped to folder-relative.
        // A binary entry emits "-" for both counts, and a pure rename with no content change emits zeroes.
        foreach (var line in output.Split(separator: '\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            var added = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : 0;
            var removed = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0;
            var relative = parts[2].Trim();
            numstat[string.Create(CultureInfo.InvariantCulture, $"{alias}/{relative}")] = new LineStat(added, removed);
        }
    }

    private static IReadOnlyList<PatchApplyFileEntry> ApplyNumstat(IReadOnlyList<PatchApplyFileEntry> files,
        IReadOnlyDictionary<string, LineStat> numstat)
    {
        return files
               .Select(file =>
               {
                   var key = string.Create(CultureInfo.InvariantCulture, $"{file.Alias}/{file.RelativePath}");
                   return numstat.TryGetValue(key, out var stats)
                       ? file with
                       {
                           Added = stats.Added,
                           Removed = stats.Removed
                       }
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
            await WriteSubPatchAsync(tempPatch, alias.SubPatch, cancellationToken);
            var fullArguments = arguments.Append(tempPatch).ToArray();
            return await runner.RunAsync(alias.ResolvedRoot, fullArguments, cancellationToken);
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
            // Every exit path, cancellation included: the `finally` covers the OperationCanceledException that
            // propagates out of the write or out of the runner, so a cancelled apply leaves no copy behind.
            TryDeleteFile(tempPatch);
        }
    }

    /// <summary>
    ///     Writes one alias's sub-patch to the system temp directory, owner-only. The mode rides on the CREATE
    ///     rather than being narrowed afterwards, which would leave a window for another local user to read it.
    ///     <see cref="SecureFilePermissions.Apply" /> follows for the Windows ACL.
    /// </summary>
    private static async Task WriteSubPatchAsync(string path, string subPatch, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(path, options);
        await using (stream)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(subPatch), cancellationToken);
        }

        SecureFilePermissions.Apply(path);
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
            return ApplyPlan.Missing("no exported patch is available for this run.");
        }

        if (fileInfo.Length == 0)
        {
            return ApplyPlan.Missing("the exported patch is empty.");
        }

        var maxPatchBytes = await _runtimeSettings.GetAgentHomeMaxPatchBytesAsync(cancellationToken);
        if (fileInfo.Length > maxPatchBytes)
        {
            return ApplyPlan.Invalid("the exported patch exceeds the maximum allowed size.");
        }

        // ONE read: the bytes hashed, parsed and cut into sub-patches are the same buffer. A second read would
        // re-open the window the caller's expected hash exists to close.
        byte[] patchBytes;
        try
        {
            patchBytes = await File.ReadAllBytesAsync(patchPath, cancellationToken);
        }
        catch (IOException)
        {
            return ApplyPlan.Invalid("the exported patch could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            return ApplyPlan.Invalid("the exported patch could not be read.");
        }

        var patchSha256 = Convert.ToHexStringLower(SHA256.HashData(patchBytes));
        if (request.ExpectedPatchSha256 is { Length: > 0 } expected
            && !string.Equals(expected, patchSha256, StringComparison.OrdinalIgnoreCase))
        {
            // Deliberately not echoing either hash: the caller already holds the one it sent, and the one on disk
            // describes a patch it was never shown.
            return ApplyPlan.Invalid("the exported patch changed since it was previewed.") with
            {
                PatchSha256 = patchSha256
            };
        }

        var patchText = DecodePatch(patchBytes);
        var blocks = SplitBlocks(patchText);
        if (blocks.Count == 0)
        {
            return ApplyPlan.Invalid("the exported patch contains no file changes.") with
            {
                PatchSha256 = patchSha256
            };
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
            return ApplyPlan.WithRejections(rejections, containsBinary) with
            {
                PatchSha256 = patchSha256
            };
        }

        var aliasPlans = new List<AliasPlan>();
        var files = new List<PatchApplyFileEntry>();
        foreach (var group in parsed.GroupBy(item => item.Alias, StringComparer.Ordinal))
        {
            var aliasPlan = await BuildAliasPlanAsync(group.Key, [.. group], rejections, cancellationToken);
            if (aliasPlan is null)
            {
                continue;
            }

            aliasPlans.Add(aliasPlan);
            files.AddRange(aliasPlan.Files);
        }

        if (rejections.Count != 0)
        {
            return ApplyPlan.WithRejections(rejections, containsBinary) with
            {
                PatchSha256 = patchSha256
            };
        }

        return new ApplyPlan
        {
            IsValid = true,
            Aliases = aliasPlans,
            Files = files,
            Rejections = rejections,
            ContainsBinary = containsBinary,
            PatchSha256 = patchSha256
        };
    }

    private async Task<AliasPlan?> BuildAliasPlanAsync(string alias, IReadOnlyList<ParsedBlock> blocks, List<string> rejections, CancellationToken cancellationToken)
    {
        // Map alias -> id -> trusted host path. Unknown/unresolvable alias rejects (fail closed).
        ResolvedSelectedFolder resolved;
        try
        {
            var references = await _resolver.ListReferencesAsync(cancellationToken);
            var reference = references.FirstOrDefault(candidate => string.Equals(candidate.Alias, alias, StringComparison.Ordinal));
            if (reference is null)
            {
                rejections.Add(string.Create(CultureInfo.InvariantCulture, $"alias '{alias}': not a registered selected folder."));
                return null;
            }

            resolved = await _resolver.ResolveAsync(reference.Id, cancellationToken);
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

        // Every target relative path, taken from the BODY lines git acts on, must resolve under the alias root. This guard
        // is authoritative and never relies on git's path validation; a symlinked intermediate that escapes is rejected.
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
        return new AliasPlan { Alias = alias, ResolvedRoot = resolvedRoot, SubPatch = subPatch, Files = files };
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

    /// <summary>
    ///     Decodes the patch bytes as <see cref="File.ReadAllTextAsync(string, CancellationToken)" /> would: UTF-8,
    ///     BOM honoured. Invalid sequences become replacement characters, so a malformed patch is rejected by the
    ///     guards instead of throwing.
    /// </summary>
    private static string DecodePatch(byte[] patchBytes)
    {
        return patchBytes.Length >= 3 && patchBytes[0] == 0xEF && patchBytes[1] == 0xBB && patchBytes[2] == 0xBF
            ? Encoding.UTF8.GetString(patchBytes, index: 3, patchBytes.Length - 3)
            : Encoding.UTF8.GetString(patchBytes);
    }

    private static bool IsValidRunId(string runId)
    {
        return !string.IsNullOrEmpty(runId) && RunIdRegex().IsMatch(runId);
    }

    private string ResolveAgentHomeRoot()
    {
        var baseRoot = string.IsNullOrWhiteSpace(_options.RootPath)
            ? Path.Combine(_dataDirectoryRoot, DefaultRootDirectoryName)
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

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RunIdRegex();

    // Matches only the residual filename after the temporary-directory prefix has already been replaced.
    [GeneratedRegex(@"agenthome-apply-[0-9a-fA-F]{32}\.patch", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TempPatchFilenameRegex();

    private sealed record AliasPlan
    {
        public required string Alias { get; init; }

        public required string ResolvedRoot { get; init; }

        public required string SubPatch { get; init; }

        public required IReadOnlyList<PatchApplyFileEntry> Files { get; init; }
    }

    private sealed record ApplyPlan
    {
        public bool IsValid { get; init; }

        public IReadOnlyList<AliasPlan> Aliases { get; init; } = [];

        public IReadOnlyList<PatchApplyFileEntry> Files { get; init; } = [];

        public IReadOnlyList<string> Rejections { get; init; } = [];

        public bool ContainsBinary { get; init; }

        public string? PatchSha256 { get; init; }

        public bool PatchMissing { get; init; }

        public static ApplyPlan Invalid(string reason)
        {
            return new ApplyPlan
            {
                IsValid = false,
                Rejections = [reason]
            };
        }

        /// <summary>There is no patch to review at all, as opposed to one that will not apply.</summary>
        public static ApplyPlan Missing(string reason)
        {
            return new ApplyPlan
            {
                IsValid = false,
                Rejections = [reason],
                PatchMissing = true
            };
        }

        public static ApplyPlan WithRejections(IReadOnlyList<string> rejections, bool containsBinary)
        {
            return new ApplyPlan
            {
                IsValid = false,
                Rejections = rejections,
                ContainsBinary = containsBinary
            };
        }
    }

    // Per-file line counts as `git apply --numstat` reports them. Binary entries and unparsable counts land as zeroes.
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct LineStat(int Added, int Removed);
}
