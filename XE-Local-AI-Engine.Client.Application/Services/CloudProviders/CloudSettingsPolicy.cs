namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

/// <summary>
///     Cross-field save policy for an Azure Foundry connection's deployment names, custom headers and operator-added
///     allowed host suffixes.
/// </summary>
/// <remarks>
///     It lives here rather than in the boundary validator because it needs the previously stored headers to tell a
///     blank secret header that resolves via the merge apart from a fresh or renamed one that has nothing to merge
///     against. Error messages carry the offending header NAME only, never a value.
/// </remarks>
public static class CloudSettingsPolicy
{
    /// <summary>
    ///     Validates the incoming header set and host suffixes against <paramref name="existingHeaders" /> (the stored
    ///     set the merge will run against). Returns every violation in declaration order; an empty list means the
    ///     request may be merged and persisted.
    /// </summary>
    public static IReadOnlyList<string> ValidateHeadersAndSuffixes(IReadOnlyList<StoredAzureFoundryHeader> headers,
        IReadOnlyList<string?> additionalAllowedHostSuffixes,
        IReadOnlyList<StoredAzureFoundryHeader> existingHeaders)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(additionalAllowedHostSuffixes);
        ArgumentNullException.ThrowIfNull(existingHeaders);

        // The shared rules first (count, names, reserved set, duplicates, values): one home, CustomHeaderRules.
        var errors = CustomHeaderRules.FindViolations([.. headers.Select(static header => new KeyValuePair<string?, string?>(header.Name, header.Value))]).ToList();

        if (additionalAllowedHostSuffixes.Count > CustomHeaderRules.MaxHostSuffixCount)
        {
            errors.Add($"A maximum of {CustomHeaderRules.MaxHostSuffixCount} allowed host suffixes is allowed.");
        }

        // Names of stored headers that are secret, so a fresh/renamed blank secret header (no stored secret to merge
        // against) is rejected here instead of throwing later in CloudCredentialStore.ValidateConfig (500 -> 400).
        var storedSecretNames = new HashSet<string>(existingHeaders
                                                    .Where(static header => header.IsSecret && !string.IsNullOrWhiteSpace(header.Name))
                                                    .Select(static header => header.Name.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers)
        {
            var name = header.Name.Trim();

            if (name.Length == 0)
            {
                // Azure-only: a secret row with neither name nor value is still a row the operator marked, not an empty one.
                if (header.IsSecret && string.IsNullOrWhiteSpace(header.Value))
                {
                    errors.Add(CustomHeaderRules.ValueWithoutNameMessage);
                }

                continue;
            }

            // A blank secret header only resolves when CloudSettingsHeaderMerge finds a stored secret of the same name; a
            // fresh or renamed one has nothing to merge against, so reject it here (400) instead of letting CloudCredentialStore.ValidateConfig throw on save (500).
            if (header.IsSecret && string.IsNullOrWhiteSpace(header.Value) && !storedSecretNames.Contains(name))
            {
                errors.Add($"Secret custom header '{name}' requires a value.");
            }
        }

        foreach (var suffix in additionalAllowedHostSuffixes)
        {
            var trimmed = suffix?.Trim() ?? string.Empty;
            if (trimmed.Length > 0 && !AzureFoundryEndpoints.ValidateHostSuffix(trimmed))
            {
                errors.Add($"Allowed host suffix '{trimmed}' is not a valid domain suffix.");
            }
        }

        return errors;
    }

    /// <summary>
    ///     Rejects deployment names that would shadow another provider's model id: an <c>ext:</c> id or a Codex catalog id.
    ///     A bare deployment name wins routing (Azure is matched first), so such a name would hijack every send to that model.
    /// </summary>
    /// <remarks>
    ///     The installed-GGUF collision needs a store read and is checked by the save endpoint. Ceiling: an Ollama tag, or a
    ///     GGUF installed after the save, can still collide; a reserved provider prefix is the upgrade path.
    /// </remarks>
    public static IReadOnlyList<string> ValidateDeploymentNames(IEnumerable<string?> deploymentNames)
    {
        ArgumentNullException.ThrowIfNull(deploymentNames);

        var errors = new List<string>();
        foreach (var name in deploymentNames.Select(static name => name?.Trim()).Where(static name => !string.IsNullOrEmpty(name)))
        {
            // Ignoring case like Azure routing does: `EXT:box/m` would otherwise route `ext:box/m` to Azure while the trust
            // resolver still answers that connection's declared locality.
            if (name!.StartsWith(ExternalModelId.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Deployment name '{name}' uses the reserved '{ExternalModelId.Scheme}' prefix of external connections.");
            }
            else if (CodexModelCatalog.IsCodexModel(name))
            {
                errors.Add($"Deployment name '{name}' is a Codex model id and would shadow it.");
            }
        }

        return errors;
    }
}
