namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>Fail-closed startup validation for the two free-form members.</summary>
/// <remarks>
///     The ranges are data annotations; these two are not expressible as one, and a misspelt path or identity that
///     only surfaced at the first install would present as a storage failure on that instance rather than as the
///     configuration error it is.
/// </remarks>
internal sealed partial class ExternalAppsOptionsValidator : IValidateOptions<ExternalAppsOptions>
{
    private const string SectionName = ExternalAppsOptions.SectionName;

    public ValidateOptionsResult Validate(string? name, ExternalAppsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.InstanceRoot is { } instanceRoot
            && (string.IsNullOrWhiteSpace(instanceRoot) || !Path.IsPathFullyQualified(instanceRoot)))
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"'{SectionName}:{nameof(ExternalAppsOptions.InstanceRoot)}' must be an absolute path when set, not '{instanceRoot}'."));
        }

        if (options.ContainerIdentity is { } identity && !ContainerIdentityRegex().IsMatch(identity))
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"'{SectionName}:{nameof(ExternalAppsOptions.ContainerIdentity)}' must be 'uid:gid' with both parts non-negative integers, not '{identity}'."));
        }

        // Checked at startup rather than at the first uninstall: the helper runs as in-container root over an instance's data, so a mistyped or merely tagged
        // reference must fail as a configuration error. The catalog's own rule, not a substring test: '@sha256:' also matches a truncated or empty digest.
        if (!ExternalAppCatalogValidator.IsDigestPinnedImage(options.StorageHelperImage))
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"'{SectionName}:{nameof(ExternalAppsOptions.StorageHelperImage)}' must be pinned as '<reference>@sha256:<64 hex digits>', not '{options.StorageHelperImage}'."));
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    [GeneratedRegex(@"^\d+:\d+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ContainerIdentityRegex();
}
