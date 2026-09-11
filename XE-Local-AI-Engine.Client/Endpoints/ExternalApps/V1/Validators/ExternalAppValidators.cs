namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Validators;

using System.Text.RegularExpressions;
using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

/// <summary>
///     Shape validation only: patterns, lengths, ranges and presence. Anything needing the catalog or the stored
///     instance — whether a variable is declared, whether the fingerprint still matches, whether the transition is
///     legal — belongs to the service, because a probe does not belong in a validator.
///     <para>
///         NO VALUE EVER REACHES A MESSAGE. The variables map carries an application's admin password and its API
///         keys, and a 400 body is logged, so every rule in this file names the KEY and never the value, and
///         <c>{PropertyValue}</c> is banned here. FluentValidation's default length message does not echo a value, but
///         one casually written message or a <c>ForEach</c> over the dictionary would.
///     </para>
/// </summary>
public static partial class ExternalAppValidationRules
{
    /// <summary>The catalog's application id grammar, restated from the manifest contract.</summary>
    public const string ApplicationIdPattern = "^[a-z][a-z0-9-]{1,40}$";

    public const string ApplicationIdMessage = "Use lowercase letters, digits and hyphens (2-41 characters).";

    /// <summary>The service name grammar, used by the log read's optional selector.</summary>
    public const string ServiceNamePattern = "^[a-z][a-z0-9-]{0,30}$";

    /// <summary>Environment-variable naming, so a key that cannot become one is refused before the service sees it.</summary>
    public const string VariableKeyPattern = "^[A-Za-z_][A-Za-z0-9_]{0,63}$";

    /// <summary>
    ///     The bound <see cref="VariableKeyPattern" /> already carries (1 + 63). It is restated as a number because the
    ///     400 message ECHOES the submitted key, and a caller that posts a ten-kilobyte key must not get ten kilobytes
    ///     of it back in a body that is logged. Every message naming a key truncates to this length.
    /// </summary>
    public const int MaxVariableKeyLength = 64;

    /// <summary>Lowercase hex, 64 characters — the shape of a SHA-256. Whether it MATCHES is the service's 409.</summary>
    public const string ManifestSha256Pattern = "^[a-f0-9]{64}$";

    public const int MaxVariableCount = 64;

    public const int MaxVariableValueLength = 4096;

    public const int MaxDisplayNameLength = 128;

    /// <summary>
    ///     Mirrors the default of <c>ExternalAppsOptions.MaxLogTailLines</c>. A SHAPE pre-check only: the node's
    ///     configured cap is the real bound and <c>IExternalAppService.ReadLogsAsync</c> enforces it, so a node that
    ///     lowered the option still rejects a tail this rule let through — with the service's own message.
    /// </summary>
    public const int MaxLogTailLines = 2000;

    public const int MaxDaemonIdLength = 128;

    public const string ExpectedVersionRequiredMessage =
        "expectedVersion is required: send the version you last read from the instance.";

    /// <summary>
    ///     The variables map's shape rules, applied identically by install, update and reconfigure so one submitted
    ///     map cannot pass one route and fail another. Every message names the key alone.
    /// </summary>
    internal static void ApplyVariables<T>(AbstractValidator<T> validator, Func<T, IReadOnlyDictionary<string, string>?> selector)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(selector);

        _ = validator.RuleFor(request => selector(request))
                     .NotNull()
                     .WithMessage("Send a variables object, empty if the application declares none.")
                     .OverridePropertyName("variables");

        _ = validator.RuleFor(request => selector(request))
                     .Must(static variables => variables is null || variables.Count <= MaxVariableCount)
                     .WithMessage($"Send at most {MaxVariableCount} variables.")
                     .OverridePropertyName("variables");

        // Ahead of the pattern rule: both refuse an over-long key, and this one names the bound the caller broke.
        _ = validator.RuleFor(request => selector(request))
                     .Must(static variables => FirstOverlongName(variables) is null)
                     .WithMessage(request =>
                         $"Variable '{Truncate(FirstOverlongName(selector(request)))}' exceeds {MaxVariableKeyLength} characters.")
                     .OverridePropertyName("variables");

        _ = validator.RuleFor(request => selector(request))
                     .Must(static variables => FirstInvalidKey(variables) is null)
                     .WithMessage(request => $"Variable '{Truncate(FirstInvalidKey(selector(request)))}' is not a valid variable name.")
                     .OverridePropertyName("variables");

        _ = validator.RuleFor(request => selector(request))
                     .Must(static variables => FirstOverlongKey(variables) is null)
                     .WithMessage(request =>
                         $"Variable '{Truncate(FirstOverlongKey(selector(request)))}' exceeds {MaxVariableValueLength} characters.")
                     .OverridePropertyName("variables");
    }

    /// <summary>The fingerprint pair install and update both echo from the preview that produced their dialog.</summary>
    internal static void ApplyManifestFingerprint<T>(AbstractValidator<T> validator,
        Func<T, int> manifestVersion,
        Func<T, string?> manifestSha256)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(manifestVersion);
        ArgumentNullException.ThrowIfNull(manifestSha256);

        _ = validator.RuleFor(request => manifestVersion(request))
                     .GreaterThanOrEqualTo(1)
                     .WithMessage("Echo the manifestVersion the preview returned.")
                     .OverridePropertyName("manifestVersion");

        _ = validator.RuleFor(request => manifestSha256(request))
                     .NotEmpty()
                     .Matches(ManifestSha256Pattern)
                     .WithMessage("Echo the manifestSha256 the preview returned.")
                     .OverridePropertyName("manifestSha256");
    }

    /// <summary>
    ///     <c>NotNull</c> IS the presence check. An omitted <c>?expectedVersion=</c> binds a nullable to null and
    ///     answers 400, while an explicit <c>0</c> passes and reaches the service — which is the difference between a
    ///     client that forgot the guard and one that supplied it. A stale value is the service's 409.
    /// </summary>
    internal static void ApplyExpectedVersion<T>(AbstractValidator<T> validator, Func<T, long?> selector)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(selector);

        _ = validator.RuleFor(request => selector(request))
                     .NotNull()
                     .WithMessage(ExpectedVersionRequiredMessage)
                     .GreaterThanOrEqualTo(0)
                     .WithMessage("expectedVersion cannot be negative.")
                     .OverridePropertyName("expectedVersion");
    }

    [GeneratedRegex(VariableKeyPattern, RegexOptions.NonBacktracking)]
    private static partial Regex VariableKeyRegex();

    private static string? FirstInvalidKey(IReadOnlyDictionary<string, string>? variables)
    {
        if (variables is null)
        {
            return null;
        }

        return variables.Where(static entry => !VariableKeyRegex().IsMatch(entry.Key))
                        .Select(static entry => entry.Key)
                        .FirstOrDefault();
    }

    private static string? FirstOverlongName(IReadOnlyDictionary<string, string>? variables)
    {
        if (variables is null)
        {
            return null;
        }

        return variables.Where(static entry => entry.Key.Length > MaxVariableKeyLength)
                        .Select(static entry => entry.Key)
                        .FirstOrDefault();
    }

    /// <summary>Bounds what a message echoes back. A key is at most 64 characters; anything longer is a caller's noise.</summary>
    private static string? Truncate(string? key)
    {
        return key is { Length: > MaxVariableKeyLength } ? key[..MaxVariableKeyLength] : key;
    }

    private static string? FirstOverlongKey(IReadOnlyDictionary<string, string>? variables)
    {
        if (variables is null)
        {
            return null;
        }

        return variables.Where(static entry => entry.Value is { Length: > MaxVariableValueLength })
                        .Select(static entry => entry.Key)
                        .FirstOrDefault();
    }
}

public sealed class InstallExternalAppRequestValidator : Validator<InstallExternalAppRequest>
{
    public InstallExternalAppRequestValidator()
    {
        _ = RuleFor(static request => request.ApplicationId)
            .NotEmpty()
            .Matches(ExternalAppValidationRules.ApplicationIdPattern)
            .WithMessage(ExternalAppValidationRules.ApplicationIdMessage)
            .OverridePropertyName("applicationId");

        _ = RuleFor(static request => request.DisplayName)
            .MaximumLength(ExternalAppValidationRules.MaxDisplayNameLength)
            .OverridePropertyName("displayName");

        // Required TRUE, not merely present: consent to what the application may do is never inferred from a caller
        // having sent the rest of the form.
        _ = RuleFor(static request => request.AcceptPermissions)
            .Equal(true)
            .WithMessage("Accept the application's declared permissions before installing.")
            .OverridePropertyName("acceptPermissions");

        ExternalAppValidationRules.ApplyManifestFingerprint(this,
            static request => request.ManifestVersion,
            static request => request.ManifestSha256);

        ExternalAppValidationRules.ApplyVariables(this, static request => request.Variables);
    }
}

/// <summary>
///     <c>AcceptPermissions</c> is deliberately NOT required true here: an update that widens nothing needs no
///     acknowledgement, and the server owns that verdict. A widening without it is a 409, not a 400.
/// </summary>
public sealed class UpdateExternalAppRequestValidator : Validator<UpdateExternalAppRequest>
{
    public UpdateExternalAppRequestValidator()
    {
        ExternalAppValidationRules.ApplyManifestFingerprint(this,
            static request => request.ManifestVersion,
            static request => request.ManifestSha256);

        ExternalAppValidationRules.ApplyVariables(this, static request => request.Variables);
        ExternalAppValidationRules.ApplyExpectedVersion(this, static request => request.ExpectedVersion);
    }
}

public sealed class UpdateExternalAppVariablesRequestValidator : Validator<UpdateExternalAppVariablesRequest>
{
    public UpdateExternalAppVariablesRequestValidator()
    {
        ExternalAppValidationRules.ApplyVariables(this, static request => request.Variables);
        ExternalAppValidationRules.ApplyExpectedVersion(this, static request => request.ExpectedVersion);
    }
}

/// <summary>Serves the four body verbs, so all four answer 400 through one rule.</summary>
public sealed class ExternalAppInstanceCommandRequestValidator : Validator<ExternalAppInstanceCommandRequest>
{
    public ExternalAppInstanceCommandRequestValidator()
    {
        ExternalAppValidationRules.ApplyExpectedVersion(this, static request => request.ExpectedVersion);
    }
}

/// <summary>The same presence rule for the uninstall, whose version arrives in the query rather than a body.</summary>
public sealed class UninstallExternalAppRequestValidator : Validator<UninstallExternalAppRequest>
{
    public UninstallExternalAppRequestValidator()
    {
        ExternalAppValidationRules.ApplyExpectedVersion(this, static request => request.ExpectedVersion);
    }
}

public sealed class ExternalAppInstanceEventFeedRequestValidator : Validator<ExternalAppInstanceEventFeedRequest>
{
    public ExternalAppInstanceEventFeedRequestValidator()
    {
        _ = RuleFor(static request => request.AfterSequence)
            .GreaterThanOrEqualTo(0)
            .WithMessage("afterSequence cannot be negative.")
            .OverridePropertyName("afterSequence");

        _ = RuleFor(static request => request.Limit)
            .InclusiveBetween(1, 500)
            .WithMessage("Request between 1 and 500 events.")
            .OverridePropertyName("limit");
    }
}

/// <summary>A tail above the cap is REJECTED, never clamped: a silently clamped request reads as a truncated log.</summary>
public sealed class ExternalAppInstanceLogsRequestValidator : Validator<ExternalAppInstanceLogsRequest>
{
    public ExternalAppInstanceLogsRequestValidator()
    {
        _ = RuleFor(static request => request.Tail)
            .InclusiveBetween(1, ExternalAppValidationRules.MaxLogTailLines)
            .WithMessage($"Request between 1 and {ExternalAppValidationRules.MaxLogTailLines} log lines.")
            .OverridePropertyName("tail");

        _ = RuleFor(static request => request.Service)
            .Matches(ExternalAppValidationRules.ServiceNamePattern)
            .When(static request => request.Service is not null)
            .WithMessage("Name one of the application's services.")
            .OverridePropertyName("service");
    }
}

public sealed class RefreshExternalAppRuntimeRequestValidator : Validator<RefreshExternalAppRuntimeRequest>
{
    public RefreshExternalAppRuntimeRequestValidator()
    {
        _ = RuleFor(static request => request.AcknowledgeDaemonId)
            .NotEmpty()
            .MaximumLength(ExternalAppValidationRules.MaxDaemonIdLength)
            .When(static request => request.AcknowledgeDaemonId is not null)
            .WithMessage("Send the daemon id the runtime panel showed, or omit the field to re-probe only.")
            .OverridePropertyName("acknowledgeDaemonId");
    }
}
