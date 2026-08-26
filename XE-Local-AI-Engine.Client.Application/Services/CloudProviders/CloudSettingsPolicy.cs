namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Cross-field save policy for an Azure Foundry connection's custom headers and operator-added allowed host
///     suffixes. Lives here — not in the boundary validator — because it needs the previously stored headers to tell a
///     blank secret header that resolves via the merge apart from a fresh or renamed one that
///     has nothing to merge against. Error messages carry the offending header NAME only, never a value.
/// </summary>
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

        var errors = new List<string>();

        if (headers.Count > AzureFoundryHeaderRules.MaxHeaderCount)
        {
            errors.Add($"A maximum of {AzureFoundryHeaderRules.MaxHeaderCount} custom headers is allowed.");
        }

        if (additionalAllowedHostSuffixes.Count > AzureFoundryHeaderRules.MaxHostSuffixCount)
        {
            errors.Add($"A maximum of {AzureFoundryHeaderRules.MaxHostSuffixCount} allowed host suffixes is allowed.");
        }

        // Names of stored headers that are secret, so a fresh/renamed blank secret header (no stored secret to merge
        // against) is rejected here instead of throwing later in CloudCredentialStore.ValidateConfig (500 -> 400).
        var storedSecretNames = new HashSet<string>(existingHeaders
                                                    .Where(static header => header.IsSecret && !string.IsNullOrWhiteSpace(header.Name))
                                                    .Select(static header => header.Name.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            var name = header.Name.Trim();

            if (name.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(header.Value) || header.IsSecret)
                {
                    errors.Add("A custom header value was provided without a header name.");
                }

                continue;
            }

            if (name.Length > AzureFoundryHeaderRules.MaxHeaderNameLength)
            {
                errors.Add($"Custom header name '{name}' exceeds {AzureFoundryHeaderRules.MaxHeaderNameLength} characters.");
            }
            else if (!AzureFoundryHeaderRules.IsValidHeaderName(name))
            {
                errors.Add($"Custom header name '{name}' contains invalid characters.");
            }
            else if (AzureFoundryHeaderRules.IsReservedName(name))
            {
                errors.Add($"Custom header name '{name}' is reserved and cannot be set.");
            }
            else if (!seenNames.Add(name))
            {
                errors.Add($"Custom header name '{name}' is duplicated.");
            }

            if ((header.Value?.Length ?? 0) > AzureFoundryHeaderRules.MaxHeaderValueLength)
            {
                errors.Add($"Custom header '{name}' value exceeds {AzureFoundryHeaderRules.MaxHeaderValueLength} characters.");
            }
            else if (!AzureFoundryHeaderRules.IsValidHeaderValue(header.Value))
            {
                errors.Add($"Custom header '{name}' value contains invalid control characters.");
            }

            // A blank secret header only resolves when CloudSettingsHeaderMerge finds a stored secret of the same name.
            // A fresh or renamed header has nothing to merge against, so reject it here (400)
            // instead of letting CloudCredentialStore.ValidateConfig throw on save (500).
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
}
