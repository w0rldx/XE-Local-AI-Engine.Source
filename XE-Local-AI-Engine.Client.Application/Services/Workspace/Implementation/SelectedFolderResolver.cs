namespace XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed partial class SelectedFolderResolver : ISelectedFolderResolver
{
    private readonly ILogger<SelectedFolderResolver> _logger;
    private readonly INodeSelectedFolderStore _store;

    public SelectedFolderResolver(INodeSelectedFolderStore store, ILogger<SelectedFolderResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(store);
        _logger = logger;
        _store = store;
    }

    public async Task<SelectedFolderReference> RegisterAsync(SelectedFolderRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var alias = NormalizeAlias(registration.Alias);
        if (!IsValidAlias(alias))
        {
            throw new SelectedFolderValidationException($"Alias '{registration.Alias}' is not a valid selected-folder alias.");
        }

        if (!IsSafeHostPath(registration.HostPath))
        {
            // Never log the raw host path; the alias is the safe identifier.
            throw new SelectedFolderValidationException($"The host path for alias '{alias}' must be an absolute, traversal-free path.");
        }

        var existing = await _store.GetByAliasAsync(alias, cancellationToken);
        if (existing is not null)
        {
            throw new SelectedFolderConflictException($"A selected folder with alias '{alias}' is already registered.");
        }

        try
        {
            var record = await _store.AddAsync(alias, registration.HostPath, registration.Mode, cancellationToken);
            _logger.LogInformation("Registered selected folder {FolderId} with alias {Alias}.", record.Id, record.Alias);
            return new SelectedFolderReference { Id = record.Id.ToString(), Alias = record.Alias };
        }
        catch (DbUpdateException exception)
        {
            // The unique alias index is the backstop when a concurrent registration races past the pre-check above.
            // Surface it as the same typed rejection the interface contract promises rather than a raw EF exception.
            throw new SelectedFolderConflictException($"A selected folder with alias '{alias}' is already registered.", exception);
        }
    }

    public async Task<IReadOnlyList<SelectedFolderReference>> ListReferencesAsync(CancellationToken cancellationToken = default)
    {
        var records = await _store.ListAsync(cancellationToken);
        return records.Select(record => new SelectedFolderReference { Id = record.Id.ToString(), Alias = record.Alias }).ToArray();
    }

    /// <remarks>
    ///     <para>
    ///         Accepts either form the node hands out: the opaque GUID, or the human-facing <b>alias</b> that Node
    ///         Settings displays and that <c>run_in_agent_home</c>'s schema advertises. The alias path used to be
    ///         missing, so a model following the documented alias form was always rejected with "is not a valid
    ///         identifier" while only the GUID — which no tool result ever shows the model — worked.
    ///     </para>
    ///     <para>
    ///         <b>GUID first, then alias.</b> An alias of GUID shape is registrable (<see cref="NormalizeAlias" />
    ///         leaves lowercase hex and hyphens untouched and <see cref="AliasShapeRegex" /> accepts the result), so the
    ///         order matters: an opaque id must never be shadowed by an alias that merely looks like one. Falling
    ///         through to the alias lookup when the id lookup misses is what keeps such an alias reachable rather than
    ///         permanently masked.
    ///     </para>
    ///     <para>
    ///         The alias is matched EXACTLY, not normalized. Stored aliases are already canonical (registration
    ///         normalizes before persisting), so an exact match on a canonical input finds precisely what exists, and
    ///         refusing to normalize here keeps this seam from quietly resolving <c>My Scratch</c> to <c>my-scratch</c>
    ///         for a caller that passes unvalidated text. Both lookups go through the same store, which returns
    ///         <see langword="null" /> for a revoked folder, so every existing guard is unchanged: alias resolution
    ///         reaches exactly the records the GUID path reaches, and no others.
    ///     </para>
    /// </remarks>
    public async Task<ResolvedSelectedFolder> ResolveAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var isFolderId = Guid.TryParse(id, out var folderId);
        var isAlias = IsValidAlias(id);

        if (!isFolderId && !isAlias)
        {
            throw new SelectedFolderValidationException($"Selected-folder id '{id}' is not a valid identifier.");
        }

        if (isFolderId && await _store.GetByIdAsync(folderId, cancellationToken) is { } byId)
        {
            return new ResolvedSelectedFolder { Id = byId.Id, Alias = byId.Alias, HostPath = byId.HostPath, Mode = byId.Mode };
        }

        // The unique index on alias (filtered to active rows) is what makes this unambiguous: at most one active
        // folder can carry a given alias, so there is no first-match choice to get wrong.
        if (isAlias && await _store.GetByAliasAsync(id, cancellationToken) is { } byAlias)
        {
            return new ResolvedSelectedFolder { Id = byAlias.Id, Alias = byAlias.Alias, HostPath = byAlias.HostPath, Mode = byAlias.Mode };
        }

        throw new SelectedFolderNotFoundException($"No selected folder is registered with id or alias '{id}'.");
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Selected-folder aliases are lowercase kebab-case by specification and are restricted to ASCII [a-z0-9-].")]
    private static string NormalizeAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return string.Empty;
        }

        var lowered = alias.Trim().ToLowerInvariant();
        var collapsed = NonAliasCharactersRegex().Replace(lowered, "-");
        return collapsed.Trim('-');
    }

    private static bool IsValidAlias(string alias)
    {
        return !string.IsNullOrEmpty(alias) && AliasShapeRegex().IsMatch(alias);
    }

    private static bool IsSafeHostPath(string hostPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath) || !Path.IsPathFullyQualified(hostPath) || hostPath.Any(char.IsControl))
        {
            return false;
        }

        // Registration-time guard against relative/traversal segments. Deep canonicalization (symlink/reparse-point
        // resolution against the copy root) is workspace copy's responsibility.
        var segments = hostPath.Replace(oldChar: '\\', newChar: '/').Split('/');
        return !segments.Any(segment => segment is "." or "..");
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex NonAliasCharactersRegex();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AliasShapeRegex();
}
