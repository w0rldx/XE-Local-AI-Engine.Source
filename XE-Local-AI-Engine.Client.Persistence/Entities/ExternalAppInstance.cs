namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One installed external application: the manifest it was installed with, the values it was configured with, and
///     where its lifecycle currently stands. Every column is plaintext structural except <see cref="VariablesJson" />,
///     which carries the user's own secrets and is the one encrypted column in the family.
/// </summary>
internal sealed record class ExternalAppInstance
{
    public Guid Id { get; set; }

    /// <summary>
    ///     The catalog application this instance was installed from. Indexed but <b>not</b> unique: the schema stays
    ///     N:1 on purpose, and the one-instance-per-application rule of V1 is enforced by the install gate, which holds
    ///     the instance lease across the check and the insert. Plaintext (structural).
    /// </summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>The <c>manifestVersion</c> of <see cref="ManifestSnapshotJson" />. Plaintext (structural).</summary>
    public int ManifestVersion { get; set; }

    /// <summary>
    ///     The exact manifest this instance was installed or last updated with, verbatim as the catalog served it.
    ///     Plaintext by design: the update flow diffs the catalog's manifest against this snapshot, and the detail view
    ///     renders the instance's own manifest rather than the catalog's current one.
    /// </summary>
    public string ManifestSnapshotJson { get; set; } = string.Empty;

    /// <summary>The manifest's display name at install time. Plaintext (structural).</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Where the instance is in its lifecycle. Plaintext (structural).</summary>
    public ExternalAppInstanceStatus Status { get; set; }

    /// <summary>What the user last asked for, which is what the boot reconciler compares the daemon against. Plaintext (structural).</summary>
    public ExternalAppDesiredState DesiredState { get; set; }

    /// <summary>
    ///     An operator pin to one container runtime, as text rather than as an enum: this project references only
    ///     <c>Providers.Abstractions</c> and cannot see the containers layer's <c>ContainerRuntimeSelection</c>, which
    ///     the application layer parses this string with. <b>V1 never populates it</b> — the omission is a decision, not
    ///     an oversight. Plaintext (structural).
    /// </summary>
    public string? RuntimeOverride { get; set; }

    /// <summary>The provider name the runtime resolved to on the last operation. Plaintext (structural).</summary>
    public string RuntimeProvider { get; set; } = string.Empty;

    /// <summary>
    ///     The instance's configured variable values as a JSON object. The one column here that holds real content —
    ///     a manifest's <c>secret</c> variables are the user's own credentials — so it is plaintext while tracked in
    ///     memory and sealed at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> under AAD column name
    ///     <c>external_app_instance_variables_json</c> with this row's own id in both the conversation and the record
    ///     slot. Required: an instance with no declared variables stores <c>{}</c>.
    /// </summary>
    public byte[] VariablesJson { get; set; } = [];

    /// <summary>
    ///     The host ports the engine bound, as <c>{"service":{"7000":41237}}</c>. Plaintext (structural): loopback port
    ///     numbers are what the Open link is composed from, and nothing about them is secret.
    /// </summary>
    public string PublishedPortsJson { get; set; } = string.Empty;

    /// <summary>Absolute host path of this instance's directory, under the node data directory. Plaintext (structural).</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>One of the closed <see cref="ExternalAppFailureCategory" /> values, or null. Plaintext (structural).</summary>
    public ExternalAppFailureCategory? FailureCategory { get; set; }

    /// <summary>
    ///     A short elaboration of <see cref="FailureCategory" />, or null. <b>Content-free by contract</b>: category
    ///     prose, a service name and, for the resource gate, requested-versus-available figures — never a variable value
    ///     and never a daemon message, which is what makes it safe to surface verbatim. Plaintext (structural).
    /// </summary>
    public string? FailureSummary { get; set; }

    /// <summary>
    ///     Set when the configured variables change and cleared when Start rebuilds the containers. A created
    ///     container's environment is immutable, so a configure that only rewrote the row would leave the running
    ///     containers on the old values with nothing recording the divergence. Plaintext (structural).
    /// </summary>
    public bool NeedsRecreate { get; set; }

    /// <summary>Unix-ms instant the install row was created. Plaintext (structural).</summary>
    public long InstalledAtUtc { get; set; }

    /// <summary>Unix-ms instant the instance last reached <c>Running</c>, or null. Plaintext (structural).</summary>
    public long? StartedAtUtc { get; set; }

    /// <summary>Unix-ms instant the instance last reached <c>Stopped</c>, or null. Plaintext (structural).</summary>
    public long? StoppedAtUtc { get; set; }

    /// <summary>Unix-ms instant of the last write to this row. Plaintext (structural).</summary>
    public long UpdatedAtUtc { get; set; }

    /// <summary>
    ///     The highest event sequence committed for this instance. Minted by the store inside the same transaction as
    ///     the status change, so the feed has no holes and no reservations. Plaintext (structural).
    /// </summary>
    public long LastSequence { get; set; }

    /// <summary>Optimistic concurrency token; every lifecycle compare-and-swap contends on it. Plaintext (structural).</summary>
    public long Version { get; set; }
}
