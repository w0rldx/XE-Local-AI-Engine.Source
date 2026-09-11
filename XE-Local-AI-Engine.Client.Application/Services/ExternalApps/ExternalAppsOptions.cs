namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>
///     Operator-configurable settings for the External Apps runtime (ADR 0010). The catalog has its own section
///     (<c>ExternalApps:Catalog</c>) bound by the catalog module, so there is deliberately no catalog member here: two
///     bound copies of one section disagree the first time one of them is read.
/// </summary>
public sealed record ExternalAppsOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "ExternalApps";

    /// <summary>
    ///     Whether the feature does anything. Default <see langword="false" />: the module still registers so the
    ///     composition root has one shape, but the reconciler and the observer return immediately and every service
    ///     entry point refuses. Registration is not the gate; behaviour is.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    ///     Absolute directory under which instance state is written, or <see langword="null" /> to use the node data
    ///     directory. Set on a box whose data directory is too small for application images' bind mounts.
    /// </summary>
    public string? InstanceRoot { get; init; }

    /// <summary>
    ///     The <c>uid:gid</c> the built-in <c>XE_UID</c>/<c>XE_GID</c> carry, overriding the daemon-derived answer.
    ///     Not a <c>--user</c>: the engine never passes one. It exists for a daemon whose identity mapping neither
    ///     the rootless nor the rootful rule describes.
    /// </summary>
    public string? ContainerIdentity { get; init; }

    /// <summary>How long one service is given to reach <c>running</c>, or <c>healthy</c> when it declares a healthcheck.</summary>
    [Range(30, 3600)]
    public int ServiceReadyTimeoutSeconds { get; init; } = 300;

    /// <summary>How long a container is given to exit on its own before the daemon kills it.</summary>
    [Range(0, 600)]
    public int StopGracePeriodSeconds { get; init; } = 30;

    /// <summary>The largest log tail a caller may ask for. A log read is a diagnostic, not a way to exhaust the node's memory.</summary>
    [Range(1, 100_000)]
    public int MaxLogTailLines { get; init; } = 2000;

    /// <summary>How often the state observer asks the daemon what the labelled containers are doing.</summary>
    [Range(5, 3600)]
    public int ObserverIntervalSeconds { get; init; } = 15;

    /// <summary>
    ///     The digest-pinned image the short-lived helper container that deletes an instance's volume CONTENTS runs.
    ///     <para>
    ///         An application's own in-container user is not the engine: under a rootless daemon a service that
    ///         creates <c>0700</c> directories as, say, uid 977 leaves them owned by a host uid inside the operator's
    ///         subuid range, which the engine can neither traverse nor unlink. The helper runs as in-container root
    ///         over one bind mount of that instance's volumes directory and deletes the contents from inside, after
    ///         which the engine removes the empty tree host-side.
    ///     </para>
    ///     <para>
    ///         The default is the same BusyBox digest the container suites pin, so a box that has run them already
    ///         holds it. Overriding it is for an air-gapped daemon that mirrors a different registry; the validator
    ///         refuses anything that is not digest-pinned, because a tag would let a different image answer to the
    ///         same name and this one runs as root over application data.
    ///     </para>
    /// </summary>
    public string StorageHelperImage { get; init; } = "busybox@sha256:9db7b59979c38555a39def84a31fb98b5296952f9e3afd4f6f11f05b07adfab0";
}

/// <summary>
///     Fail-closed startup validation for the two free-form members. The ranges are data annotations; these two are
///     not expressible as one, and a misspelt path or identity that only surfaced at the first install would surface
///     as a storage failure on the instance rather than as the configuration error it is.
/// </summary>
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

        // Checked at startup rather than at the first uninstall: the helper runs as in-container root over an
        // instance's data, so a mistyped or merely tagged reference must fail as the configuration error it is and
        // not as a storage failure on the one instance that happened to be removed first. The catalog's own rule
        // rather than a substring test: '@sha256:' also matches a truncated digest and an empty reference.
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
