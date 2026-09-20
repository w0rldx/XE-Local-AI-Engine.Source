namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     A deployment could not be planned because a value it needs is missing or malformed: an unresolved
///     <c>${TOKEN}</c>, or a <c>${</c> that does not close on a valid one.
/// </summary>
/// <remarks>
///     A Compose-style default such as <c>${FOO:-bar}</c> is this, not a literal: the engine does not implement that
///     syntax, and passing it through verbatim would put that text into a container as if it were a password. An
///     installed snapshot can predate the validator that admitted it, so the planner refuses rather than trusting
///     that nothing malformed can reach it.
/// </remarks>
public sealed class ExternalAppConfigurationException : Exception
{
    public ExternalAppConfigurationException(string message) : base(message)
    {
    }

    public ExternalAppConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ExternalAppConfigurationException()
    {
    }
}

/// <summary>
///     A manifest cannot be deployed as written: a <c>dependsOn</c> cycle, a mount collision, a service naming a
///     dependency that does not exist.
/// </summary>
/// <remarks>
///     The catalog validator states most of these rules too. This one is the control rather than a duplicate: the
///     planner deploys the SNAPSHOT stored with the instance, which was admitted by whatever validator shipped at
///     install time, and an update path that trusted the snapshot would be trusting a document no current rule has
///     read.
/// </remarks>
public sealed class ExternalAppManifestException : Exception
{
    public ExternalAppManifestException(string message) : base(message)
    {
    }

    public ExternalAppManifestException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ExternalAppManifestException()
    {
    }
}

/// <summary>
///     The feature is switched off (<c>ExternalApps:Enabled</c> is <see langword="false" />). Every entry point
///     throws it before reading anything.
/// </summary>
/// <remarks>
///     It guards a DIRECT service caller and is not the API's answer: the surface's 404 comes from the middleware
///     that removes the endpoints while the feature is off, so no request reaches a service entry point to raise
///     this. No handler maps it, and a path that let it reach HTTP would be answered as a 500 — the honest answer to
///     a request that should not have been routable.
/// </remarks>
public sealed class ExternalAppsDisabledException : Exception
{
    public ExternalAppsDisabledException() : base("External applications are not enabled on this node.")
    {
    }

    public ExternalAppsDisabledException(string message) : base(message)
    {
    }

    public ExternalAppsDisabledException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
///     No such instance, or no such application in the catalog — including an installed application that has since
///     left the catalog, which is what an update against it is refused with.
/// </summary>
public sealed class ExternalAppNotFoundException : Exception
{
    public ExternalAppNotFoundException(string message) : base(message)
    {
    }

    public ExternalAppNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ExternalAppNotFoundException()
    {
    }
}

/// <summary>Another operation already holds this instance.</summary>
/// <remarks>
///     Thrown by the gate BEFORE any status is read, so a caller cannot distinguish it from a status check it would
///     also fail: there is one answer for "busy", and it does not depend on how far the other operation got.
/// </remarks>
public sealed class ExternalAppOperationInFlightException : Exception
{
    public ExternalAppOperationInFlightException(string message) : base(message)
    {
    }

    public ExternalAppOperationInFlightException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ExternalAppOperationInFlightException()
    {
    }
}

/// <summary>
///     The operation is not defined for the status the instance is in. Also the answer to a cancel with nothing
///     running: a transient status with no live operation is a crashed one, which the boot reconciler settles rather
///     than a cancel.
/// </summary>
public sealed class ExternalAppInvalidTransitionException : Exception
{
    public ExternalAppInvalidTransitionException(string message) : base(message)
    {
    }

    public ExternalAppInvalidTransitionException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ExternalAppInvalidTransitionException()
    {
    }
}

/// <summary>This application already has an instance; V1 allows exactly one per application.</summary>
/// <remarks>
///     Enforced by a read inside the application-level lock rather than by a unique index, because the rule is "one
///     LIVE instance" and a uniqueness constraint would also outlive an uninstall that could not delete its row.
/// </remarks>
public sealed class ExternalAppAlreadyInstalledException : Exception
{
    public ExternalAppAlreadyInstalledException(string message) : base(message)
    {
    }

    public ExternalAppAlreadyInstalledException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ExternalAppAlreadyInstalledException()
    {
    }
}

/// <summary>The command did not acknowledge the permissions the application would be granted.</summary>
/// <remarks>
///     It carries the names that needed acknowledging — the whole effective set on an install, the widening alone on
///     an update — so the caller re-shows the disclosure instead of guessing what changed. Consent is never inferred:
///     a command that carries variables and omits the acknowledgement is refused rather than read as "they must have
///     seen it".
/// </remarks>
public sealed class ExternalAppPermissionChangeRequiresAcknowledgementException : Exception
{
    public ExternalAppPermissionChangeRequiresAcknowledgementException(string message, IReadOnlyList<string> addedPermissions)
        : base(message)
    {
        AddedPermissions = addedPermissions ?? [];
    }

    public ExternalAppPermissionChangeRequiresAcknowledgementException(string message) : base(message)
    {
        AddedPermissions = [];
    }

    public ExternalAppPermissionChangeRequiresAcknowledgementException(string message, Exception innerException)
        : base(message, innerException)
    {
        AddedPermissions = [];
    }

    public ExternalAppPermissionChangeRequiresAcknowledgementException()
    {
        AddedPermissions = [];
    }

    /// <summary>The permission NAMES from the fixed vocabulary. Never a value, never a path.</summary>
    public IReadOnlyList<string> AddedPermissions { get; }
}

/// <summary>
///     The manifest moved between the disclosure and the command. Carries the fingerprint as SERVED, so the caller
///     re-fetches the preview and re-shows the permission step against the manifest it will actually get.
/// </summary>
public sealed class ExternalAppManifestChangedException : Exception
{
    public ExternalAppManifestChangedException(string message, int manifestVersion, string manifestSha256)
        : base(message)
    {
        ManifestVersion = manifestVersion;
        ManifestSha256 = manifestSha256 ?? string.Empty;
    }

    public ExternalAppManifestChangedException(string message) : base(message)
    {
        ManifestSha256 = string.Empty;
    }

    public ExternalAppManifestChangedException(string message, Exception innerException) : base(message, innerException)
    {
        ManifestSha256 = string.Empty;
    }

    public ExternalAppManifestChangedException()
    {
        ManifestSha256 = string.Empty;
    }

    /// <summary>The manifest version the catalog serves now.</summary>
    public int ManifestVersion { get; }

    /// <summary>The fingerprint of the manifest the catalog serves now.</summary>
    public string ManifestSha256 { get; }
}

/// <summary>
///     The caller's <c>expectedVersion</c> is stale, or a user-initiated compare-and-swap lost to another writer.
///     Both are the same fact: the row is not what the caller believed when it decided to act.
/// </summary>
public sealed class ExternalAppConcurrencyException : Exception
{
    public ExternalAppConcurrencyException(string message) : base(message)
    {
    }

    public ExternalAppConcurrencyException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ExternalAppConcurrencyException()
    {
    }
}

/// <summary>
///     A supplied variable is missing, of the wrong type, or outside its declared constraints — or a log tail is
///     outside the permitted range.
/// </summary>
/// <remarks>
///     It carries the offending variable NAMES and never their values: the exception is rendered in a browser and
///     written to a log, and the value is what the user was asked to keep secret.
/// </remarks>
public sealed class ExternalAppValidationException : Exception
{
    public ExternalAppValidationException(string message, IReadOnlyList<string> names) : base(message)
    {
        Names = names ?? [];
    }

    public ExternalAppValidationException(string message) : base(message)
    {
        Names = [];
    }

    public ExternalAppValidationException(string message, Exception innerException) : base(message, innerException)
    {
        Names = [];
    }

    public ExternalAppValidationException()
    {
        Names = [];
    }

    /// <summary>The names that were rejected, in declaration order.</summary>
    public IReadOnlyList<string> Names { get; }
}
