namespace XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed partial class SelectedFolderResolver(INodeSelectedFolderStore store, ILogger<SelectedFolderResolver> logger) : ISelectedFolderResolver
{
    private readonly ILogger<SelectedFolderResolver> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly INodeSelectedFolderStore _store = store ?? throw new ArgumentNullException(nameof(store));

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

        var existing = await _store.GetByAliasAsync(alias, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new SelectedFolderValidationException($"A selected folder with alias '{alias}' is already registered.");
        }

        try
        {
            var record = await _store.AddAsync(alias, registration.HostPath, registration.Mode, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Registered selected folder {FolderId} with alias {Alias}.", record.Id, record.Alias);
            return new SelectedFolderReference(record.Id.ToString(), record.Alias);
        }
        catch (DbUpdateException exception)
        {
            // The unique alias index is the backstop when a concurrent registration races past the pre-check above.
            // Surface it as the same typed rejection the interface contract promises rather than a raw EF exception.
            throw new SelectedFolderValidationException($"A selected folder with alias '{alias}' is already registered.", exception);
        }
    }

    public async Task<IReadOnlyList<SelectedFolderReference>> ListReferencesAsync(CancellationToken cancellationToken = default)
    {
        var records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return records.Select(record => new SelectedFolderReference(record.Id.ToString(), record.Alias)).ToArray();
    }

    public async Task<ResolvedSelectedFolder> ResolveAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (!Guid.TryParse(id, out var folderId))
        {
            throw new SelectedFolderValidationException($"Selected-folder id '{id}' is not a valid identifier.");
        }

        var record = await _store.GetByIdAsync(folderId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            throw new SelectedFolderValidationException($"No selected folder is registered with id '{id}'.");
        }

        return new ResolvedSelectedFolder(record.Id, record.Alias, record.HostPath, record.Mode);
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
