namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

public sealed class SaveCloudSettingsEndpoint(
    ICloudCredentialStore cloudCredentialStore,
    ICapabilityReporter capabilityReporter,
    ILogger<SaveCloudSettingsEndpoint> logger) : Endpoint<SaveCloudSettingsRequest, CloudSettingsResponse>
{
    private readonly ICapabilityReporter _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
    private readonly ICloudCredentialStore _cloudCredentialStore = cloudCredentialStore ?? throw new ArgumentNullException(nameof(cloudCredentialStore));
    private readonly ILogger<SaveCloudSettingsEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override void Configure()
    {
        Put(LocalApiRoutes.CloudSettings.Settings);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SaveCloudSettingsRequest req, CancellationToken ct)
    {
        // Load prior state so a secret header re-sent with a blank value keeps its stored value (Locked #12). The mapper
        // stays pure — the merge is the only impure step and it runs here. Loaded before validation so the validator can
        // tell a fresh/renamed blank secret header (rejected, 400) apart from one that resolves via the stored merge.
        var existing = await _cloudCredentialStore.LoadConfigAsync(ct).ConfigureAwait(false);
        var existingHeaders = existing?.AzureFoundry?.Headers ?? [];

        if (!ValidateHeadersAndSuffixes(req, existingHeaders))
        {
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var mergedHeaders = CloudSettingsHeaderMerge.Merge(existingHeaders, req.Headers);

        var config = req.ToStoredConfig(mergedHeaders);
        await _cloudCredentialStore.SaveConfigAsync(config, ct).ConfigureAwait(false);
        await TryReportCapabilitiesAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(config.ToResponse(), ct).ConfigureAwait(false);
    }

    // Reserved / charset / caps / host-suffix validation. Error messages carry the offending header NAME only — never a
    // value (Locked #6–#9, #14). Returns false when any error was added.
    private bool ValidateHeadersAndSuffixes(SaveCloudSettingsRequest req, IReadOnlyList<StoredAzureFoundryHeader> existingHeaders)
    {
        var ok = true;

        void Fail(string message)
        {
            AddError(message);
            ok = false;
        }

        if (req.Headers.Count > AzureFoundryHeaderRules.MaxHeaderCount)
        {
            Fail($"A maximum of {AzureFoundryHeaderRules.MaxHeaderCount} custom headers is allowed.");
        }

        if (req.AdditionalAllowedHostSuffixes.Count > AzureFoundryHeaderRules.MaxHostSuffixCount)
        {
            Fail($"A maximum of {AzureFoundryHeaderRules.MaxHostSuffixCount} allowed host suffixes is allowed.");
        }

        // Names of stored headers that are secret, so a fresh/renamed blank secret header (no stored secret to merge
        // against) is rejected here instead of throwing later in CloudCredentialStore.ValidateConfig (500 -> 400).
        var storedSecretNames = new HashSet<string>(
            existingHeaders
                .Where(static header => header.IsSecret && !string.IsNullOrWhiteSpace(header.Name))
                .Select(static header => header.Name.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in req.Headers)
        {
            var name = header.Name?.Trim() ?? string.Empty;

            if (name.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(header.Value) || header.IsSecret)
                {
                    Fail("A custom header value was provided without a header name.");
                }

                continue;
            }

            if (name.Length > AzureFoundryHeaderRules.MaxHeaderNameLength)
            {
                Fail($"Custom header name '{name}' exceeds {AzureFoundryHeaderRules.MaxHeaderNameLength} characters.");
            }
            else if (!AzureFoundryHeaderRules.IsValidHeaderName(name))
            {
                Fail($"Custom header name '{name}' contains invalid characters.");
            }
            else if (AzureFoundryHeaderRules.IsReservedName(name))
            {
                Fail($"Custom header name '{name}' is reserved and cannot be set.");
            }
            else if (!seenNames.Add(name))
            {
                Fail($"Custom header name '{name}' is duplicated.");
            }

            if ((header.Value?.Length ?? 0) > AzureFoundryHeaderRules.MaxHeaderValueLength)
            {
                Fail($"Custom header '{name}' value exceeds {AzureFoundryHeaderRules.MaxHeaderValueLength} characters.");
            }
            else if (!AzureFoundryHeaderRules.IsValidHeaderValue(header.Value))
            {
                Fail($"Custom header '{name}' value contains invalid control characters.");
            }

            // A blank secret header only resolves when it merges with a stored secret of the same name (Locked #10/#12,
            // CloudSettingsHeaderMerge). A fresh or renamed header has nothing to merge against, so reject it here (400)
            // instead of letting CloudCredentialStore.ValidateConfig throw on save (500).
            if (header.IsSecret && string.IsNullOrWhiteSpace(header.Value) && !storedSecretNames.Contains(name))
            {
                Fail($"Secret custom header '{name}' requires a value.");
            }
        }

        foreach (var suffix in req.AdditionalAllowedHostSuffixes)
        {
            var trimmed = suffix?.Trim() ?? string.Empty;
            if (trimmed.Length > 0 && !AzureFoundryEndpoints.ValidateHostSuffix(trimmed))
            {
                Fail($"Allowed host suffix '{trimmed}' is not a valid domain suffix.");
            }
        }

        return ok;
    }

    private async Task TryReportCapabilitiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _capabilityReporter.ReportToApiAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to report capabilities after cloud settings were saved.");
        }
    }
}
