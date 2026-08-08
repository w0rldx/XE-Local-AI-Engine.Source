namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Caching.Memory;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The two-phase skill import pipeline. Phase 1 fetches, extracts, parses, validates and reports without writing;
///     phase 2 persists the report's materialised payload.
/// </summary>
/// <remarks>
///     <para>
///         Validation authority for the skill name and description is <see cref="AgentSkillFrontmatter" /> — the same
///         code MAF runs when the resolved skill is built — so nothing can be imported that would later throw at
///         agent-construction time. On top of that, this class owns the charset guards the specification does not
///         define but a model-facing string needs: a bundled file's <c>name</c> is shown to the operator, handed to the
///         model, and written to logs, so a newline in it would inject instructions above the reviewed body, and a
///         homoglyph or a bidi override would make the preview render differently from what is stored — which is
///         exactly the audit the operator's approval depends on. ASCII-only there is deliberate.
///     </para>
///     <para>
///         <c>allowed-tools</c> is stored and displayed and grants and restricts nothing. The specification is explicit
///         that it is pre-approval, not restriction: wiring it to the tool policy would be security theatre, telling
///         the operator a skill is confined when every tool remains callable.
///     </para>
/// </remarks>
internal sealed partial class SkillImportService : ISkillImportService
{
    /// <summary>Matches the body cap <c>AgentSkillService</c> applies to hand-authored skills.</summary>
    private const int MaxBodyLength = 20000;

    private const int MaxResourceNameLength = 200;
    private const int MaxDescriptionLength = 1024;
    private const int MaxOptionalFieldLength = 512;
    private const string UploadSourceUri = "upload";

    /// <summary>
    ///     How long a preview stays redeemable. Short because the cached payload holds the decrypted content of every
    ///     discovered skill, and because an approval the operator has forgotten about is not an approval.
    /// </summary>
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);

    private readonly IMemoryCache _cache;
    private readonly GitHubSkillArchiveDownloader _downloader;
    private readonly SkillImportOptions _options;
    private readonly IAgentSkillStore _store;
    private readonly TimeProvider _timeProvider;

    public SkillImportService(IAgentSkillStore store,
        IMemoryCache cache,
        TimeProvider timeProvider,
        HttpClient httpClient,
        SkillImportOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _downloader = new GitHubSkillArchiveDownloader(httpClient, options);
    }

    public Task<SkillImportPreview> PreviewArchiveAsync(ReadOnlyMemory<byte> archive, CancellationToken cancellationToken = default)
    {
        return BuildPreviewAsync(SkillArchiveReader.Read(archive, _options), UploadSourceUri, cancellationToken);
    }

    public Task<SkillImportPreview> PreviewMarkdownAsync(string skillMarkdown, CancellationToken cancellationToken = default)
    {
        // A pasted document has no containing directory, so the frontmatter name is all there is to go on, and there
        // are no bundled files or scripts to weigh. Provenance is still the upload kind — the operator's own paste is
        // no more trusted than the archive it was copied out of.
        var folder = new SkillArchiveFolder(DirectoryName: string.Empty, RootPath: string.Empty, skillMarkdown ?? string.Empty, [], []);
        return BuildPreviewAsync([folder], UploadSourceUri, cancellationToken);
    }

    public async Task<SkillImportPreview> PreviewGitHubRepositoryAsync(string owner, string repository, CancellationToken cancellationToken = default)
    {
        var archive = await _downloader.DownloadAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        return await BuildPreviewAsync(SkillArchiveReader.Read(archive, _options), $"github:{owner}/{repository}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<SkillImportResult> CommitAsync(SkillImportCommitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Checked before the token is even looked up, so an unacknowledged commit cannot consume a preview either.
        if (!request.Acknowledged)
        {
            throw new SkillImportException("The import must be explicitly acknowledged before any skill is written.");
        }

        if (!_cache.TryGetValue(CacheKey(request.Token), out SkillImportPreview? preview) || preview is null)
        {
            throw new SkillImportException("The import preview has expired or was already used. Preview the source again.");
        }

        var selected = SelectSkills(preview, request.SkillNames);
        var outcomes = new List<SkillImportOutcome>(selected.Count);
        foreach (var candidate in selected)
        {
            outcomes.Add(await PersistAsync(candidate, preview.SourceUri, request.ConflictResolution, cancellationToken).ConfigureAwait(false));
        }

        // Single-use: consumed only once the writes succeeded, so a failed commit can be retried against the same
        // approved payload rather than forcing a re-fetch that could return different content.
        _cache.Remove(CacheKey(request.Token));
        return new SkillImportResult(outcomes);
    }

    /// <summary>Resolves the caller's selection against the approved report, rejecting the whole commit before any write if it does not line up.</summary>
    private static List<SkillImportCandidate> SelectSkills(SkillImportPreview preview, IReadOnlyList<string>? names)
    {
        if (names is null || names.Count == 0)
        {
            throw new SkillImportException("No skill was selected for import.");
        }

        var selected = new List<SkillImportCandidate>(names.Count);
        foreach (var name in names)
        {
            var candidate = preview.Skills.FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase));
            if (candidate is null || !candidate.CanImport)
            {
                throw new SkillImportException("The selection does not match the approved preview. Preview the source again.");
            }

            selected.Add(candidate);
        }

        return selected;
    }

    private async Task<SkillImportOutcome> PersistAsync(SkillImportCandidate candidate,
        string sourceUri,
        SkillImportConflictResolution resolution,
        CancellationToken cancellationToken)
    {
        // Re-read at commit time: a skill with this name may have appeared since the preview was taken.
        var existing = (await _store.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(skill => string.Equals(skill.Name, candidate.Name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null && resolution == SkillImportConflictResolution.Skip)
        {
            return new SkillImportOutcome(candidate.Name, SkillImportStatus.Skipped, "A skill with this name already exists.");
        }

        // Enabled: false and Origin: Imported are not defaults to be overridden — they are the control. The definition
        // resolver only resolves enabled skills, so third-party instructions stay inert until an operator turns them on.
        var input = new AgentSkillInput(candidate.Name,
            candidate.Description,
            candidate.Body,
            Enabled: false,
            candidate.License,
            candidate.Compatibility,
            candidate.AllowedTools,
            candidate.Metadata,
            AgentSkillOrigin.Imported,
            sourceUri,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            ContentSha256(candidate));

        var stored = existing is null
            ? await _store.CreateAsync(input, cancellationToken).ConfigureAwait(false)
            : await _store.UpdateAsync(existing.Id, input, cancellationToken).ConfigureAwait(false);

        if (stored is null)
        {
            return new SkillImportOutcome(candidate.Name, SkillImportStatus.Skipped, "The existing skill was removed while the import was running.");
        }

        var resources = candidate.Resources
                                 .Select(static resource => new AgentSkillResourceInput(resource.Name, resource.Description, resource.MediaType, resource.Content))
                                 .ToList();
        await _store.ReplaceResourcesAsync(stored.Id, resources, cancellationToken).ConfigureAwait(false);

        return new SkillImportOutcome(candidate.Name, existing is null ? SkillImportStatus.Imported : SkillImportStatus.Replaced);
    }

    private async Task<SkillImportPreview> BuildPreviewAsync(IReadOnlyList<SkillArchiveFolder> folders, string sourceUri, CancellationToken cancellationToken)
    {
        var existingNames = (await _store.ListAsync(cancellationToken).ConfigureAwait(false))
                            .Select(static skill => skill.Name)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var warnings = new List<string>();
        var candidates = folders.Select(folder => BuildCandidate(folder, existingNames, warnings, _options))
                                .OrderBy(static candidate => candidate.Name, StringComparer.Ordinal)
                                .ToList();

        // Two scan roots resolving to the same skill name would make the report ambiguous and the persist order
        // decide which content wins — the same preview/persist divergence the duplicate-entry guard closes.
        if (candidates.Select(static candidate => candidate.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != candidates.Count)
        {
            throw new SkillImportException("The source contains more than one skill with the same name.");
        }

        var token = Guid.NewGuid();
        var preview = new SkillImportPreview(token, sourceUri, candidates, warnings);
        _cache.Set(CacheKey(token), preview, PreviewLifetime);
        return preview;
    }

    private static SkillImportCandidate BuildCandidate(SkillArchiveFolder folder,
        HashSet<string> existingNames,
        List<string> warnings,
        SkillImportOptions options)
    {
        var problems = new List<string>();
        if (!SkillFrontmatterReader.TryRead(folder.SkillMarkdown, out var frontmatter, out var parseError) || frontmatter is null)
        {
            return Unimportable(folder.DirectoryName, [parseError ?? "The skill frontmatter could not be read."]);
        }

        // Specification rule: a skill's name must match its containing directory. Where they disagree the directory
        // wins, because that is what the ecosystem addresses the skill by — but the operator is told.
        var name = folder.DirectoryName.Length > 0 ? folder.DirectoryName : frontmatter.Name ?? string.Empty;
        if (folder.DirectoryName.Length > 0 && frontmatter.Name is not null && !string.Equals(frontmatter.Name, folder.DirectoryName, StringComparison.Ordinal))
        {
            warnings.Add($"A skill declared a name that differs from its folder; the folder name '{folder.DirectoryName}' was used.");
        }

        ValidateFrontmatter(name, frontmatter, problems);
        ValidateBody(frontmatter.Body, problems);
        var resources = BuildResources(folder, problems, options);

        return new SkillImportCandidate(name,
            frontmatter.Description ?? string.Empty,
            frontmatter.Body,
            Optional(frontmatter.License),
            Optional(frontmatter.Compatibility),
            Optional(frontmatter.AllowedTools),
            SafeMetadata(frontmatter.Metadata, problems),
            Encoding.UTF8.GetByteCount(frontmatter.Body),
            frontmatter.Body.Length == 0 ? 0 : frontmatter.Body.Count(static character => character == '\n') + 1,
            resources,
            folder.RefusedScripts,
            existingNames.Contains(name),
            problems);
    }

    private static void ValidateFrontmatter(string name, SkillFrontmatterDocument frontmatter, List<string> problems)
    {
        // MAAI001: Agent Skills ship [Experimental]; the scoped suppression matches AgentSkillService. These are the
        // same validators the AgentInlineSkill constructor runs, and their messages echo no caller content.
#pragma warning disable MAAI001
        if (!AgentSkillFrontmatter.ValidateName(name, out var nameError))
        {
            problems.Add(nameError);
        }

        if (string.IsNullOrWhiteSpace(frontmatter.Description))
        {
            problems.Add("The skill declares no description.");
        }
        else if (!IsSafeText(frontmatter.Description, MaxDescriptionLength))
        {
            // A control character in a description is not a typo: the description is the one skill string the model
            // sees before it decides to load anything, so a newline there injects instructions into the tool listing.
            problems.Add("The skill description contains control characters or is too long.");
        }
        else if (!AgentSkillFrontmatter.ValidateDescription(frontmatter.Description, out var descriptionError))
        {
            problems.Add(descriptionError);
        }
#pragma warning restore MAAI001

        if (!IsSafeText(frontmatter.License, MaxOptionalFieldLength)
            || !IsSafeText(frontmatter.Compatibility, MaxOptionalFieldLength)
            || !IsSafeText(frontmatter.AllowedTools, MaxOptionalFieldLength))
        {
            problems.Add("An optional frontmatter field contains control characters or is too long.");
        }
    }

    private static void ValidateBody(string body, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            problems.Add("The skill has no instructions below its frontmatter.");
        }
        else if (body.Length > MaxBodyLength)
        {
            problems.Add($"The skill body is longer than the {MaxBodyLength} character limit.");
        }
    }

    private static IReadOnlyList<SkillImportResource> BuildResources(SkillArchiveFolder folder, List<string> problems, SkillImportOptions options)
    {
        if (folder.ResourceLimitExceeded)
        {
            problems.Add($"The skill bundles more than the {options.MaxResourcesPerSkill} files a single skill may carry.");
        }

        var resources = new List<SkillImportResource>(folder.Files.Count);
        var rejected = 0;
        foreach (var file in folder.Files)
        {
            if (!IsSafeResourceName(file.Name))
            {
                rejected++;
                continue;
            }

            // The name doubles as the description: it is the lookup key the model is told to use verbatim, and it is
            // the only string here that has already passed the charset guard.
            resources.Add(new SkillImportResource(file.Name, file.Name, file.MediaType, file.Content, Encoding.UTF8.GetByteCount(file.Content)));
        }

        if (rejected > 0)
        {
            // Deliberately not echoed. The rejected name is the injection payload; repeating it in the report would
            // put it on the very approval surface the guard exists to protect.
            problems.Add($"{rejected} bundled file name(s) use characters that are not allowed (letters, digits, '.', '_', '-' and '/' only).");
        }

        return resources;
    }

    private static IReadOnlyDictionary<string, string>? SafeMetadata(IReadOnlyDictionary<string, string>? metadata, List<string> problems)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        if (metadata.Any(pair => !IsSafeText(pair.Key, MaxOptionalFieldLength) || !IsSafeText(pair.Value, MaxOptionalFieldLength)))
        {
            problems.Add("A frontmatter metadata entry contains control characters or is too long.");
            return null;
        }

        return metadata;
    }

    /// <summary>
    ///     A bundled file's name is model-facing, approval-facing and a log field at once, so it is held to a strict
    ///     ASCII path charset with an explicit <c>..</c> rejection (which the pattern's segment class would otherwise
    ///     admit). Rejecting non-ASCII is the point, not an oversight: a homoglyph or a U+202E override renders one way
    ///     in the preview and stores another, which defeats the operator's audit.
    /// </summary>
    private static bool IsSafeResourceName(string name)
    {
        return name.Length is > 0 and <= MaxResourceNameLength
               && ResourceNamePattern().IsMatch(name)
               && !name.Split('/').Contains("..", StringComparer.Ordinal);
    }

    private static bool IsSafeText(string? value, int maxLength)
    {
        return value is null || (value.Length <= maxLength && !value.Any(char.IsControl));
    }

    private static string? Optional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static SkillImportCandidate Unimportable(string name, IReadOnlyList<string> problems)
    {
        return new SkillImportCandidate(name,
            string.Empty,
            string.Empty,
            License: null,
            Compatibility: null,
            AllowedTools: null,
            Metadata: null,
            BodySizeBytes: 0,
            BodyLineCount: 0,
            [],
            [],
            ConflictsWithExistingSkill: false,
            problems);
    }

    /// <summary>Digest over the canonical payload, for change detection on a later re-import. It is not a trust signal — nothing in this ecosystem signs skills.</summary>
    private static string ContentSha256(SkillImportCandidate candidate)
    {
        var builder = new StringBuilder().Append(candidate.Name).Append('\n')
                                         .Append(candidate.Description).Append('\n')
                                         .Append(candidate.Body).Append('\n');
        foreach (var resource in candidate.Resources.OrderBy(static resource => resource.Name, StringComparer.Ordinal))
        {
            builder.Append(resource.Name).Append('\n').Append(resource.Content).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string CacheKey(Guid token)
    {
        return $"skill-import:{token:N}";
    }

    [GeneratedRegex("^(?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ResourceNamePattern();
}
