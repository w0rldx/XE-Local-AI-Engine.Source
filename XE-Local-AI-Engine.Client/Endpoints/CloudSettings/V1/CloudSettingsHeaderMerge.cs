namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Secret-preserving merge of incoming custom headers against the previously stored set (Locked #10/#12). Lives in
///     the endpoint (not the pure mapper): the stored value is inherited ONLY when a header is sent secret with a blank
///     value AND matches a stored secret header of the same name. A secret→non-secret transition, a rename, or any
///     non-secret blank value NEVER inherits — there is no secret resurrection on toggle or rename.
/// </summary>
internal static class CloudSettingsHeaderMerge
{
    public static IReadOnlyList<StoredAzureFoundryHeader> Merge(IReadOnlyList<StoredAzureFoundryHeader> existing,
        IReadOnlyList<SaveAzureFoundryHeaderRequest> incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);

        var storedByName = new Dictionary<string, StoredAzureFoundryHeader>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in existing.Where(static header => !string.IsNullOrWhiteSpace(header.Name)))
        {
            storedByName[header.Name.Trim()] = header;
        }

        var merged = new List<StoredAzureFoundryHeader>(incoming.Count);
        foreach (var header in incoming)
        {
            var name = header.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                // Drop a fully blank row (mirrors the frontend); a blank name with a value is rejected by validation.
                continue;
            }

            var stored = storedByName.TryGetValue(name, out var match) ? match : null;
            var keepStored = header.IsSecret
                             && stored is { IsSecret: true }
                             && string.IsNullOrWhiteSpace(header.Value);

            merged.Add(new StoredAzureFoundryHeader
            {
                Name = name,
                Value = keepStored ? stored?.Value : header.Value,
                IsSecret = header.IsSecret
            });
        }

        return merged;
    }
}
