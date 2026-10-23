namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     External Apps: the curated catalog, the container runtime this node resolves against, and the installed
    ///     instances an operator installs, configures, starts, updates, resets and uninstalls.
    /// </summary>
    /// <remarks>
    ///     Gated on <c>ExternalApps:Enabled</c> by request-path middleware in <c>Program</c> that 404s anything under
    ///     <see cref="Root" />, the hub path included since it shares the first segment — which is why the prefix is a
    ///     constant rather than spelled at each route. Every route is <c>NodeAuthorizationPolicies.Operator</c>:
    ///     installing an application container is an administrative act, and the catalog reveals what this node can
    ///     run. <c>expectedVersion</c> and the manifest fingerprint are request members, never route segments.
    /// </remarks>
    public static class ExternalApps
    {
        public const string Root = "external-apps";

        /// <summary>Resolved runtime, capabilities and daemon identity, for the Runtime panel. A pure read: it never pins a daemon.</summary>
        public const string Runtime = "external-apps/runtime";

        /// <summary>
        ///     Re-runs the preflight and, when the body names the daemon currently observed, records the approval and
        ///     re-runs the startup reconciler.
        /// </summary>
        /// <remarks>
        ///     A POST because a refresh, a prefetch or a health check must not be able to approve whatever daemon is
        ///     answering.
        /// </remarks>
        public const string RuntimeRefresh = "external-apps/runtime/refresh";

        /// <summary>
        ///     Catalog summaries; POST refreshes past the TTL and falls back to the last-good document, naming the
        ///     failure. The base64 <c>files[]</c> bodies never cross the wire, here or on the by-id manifest read.
        /// </summary>
        public const string Catalog = "external-apps/catalog";

        public const string CatalogRefresh = "external-apps/catalog/refresh";

        public const string CatalogApplicationById = "external-apps/catalog/{applicationId}";

        /// <summary>What the install dialog needs BEFORE it asks for anything: permissions, required variables, the resource verdict, the runtime resolution.</summary>
        public const string InstallPreview = "external-apps/catalog/{applicationId}/install-preview";

        /// <summary>
        ///     GET lists installed instances; POST installs one. One instance per application in V1, so a second POST
        ///     409s. DELETE on <see cref="InstanceById" /> uninstalls AND deletes the instance's data directory — both
        ///     explicit operator acts — and carries <c>?expectedVersion=</c>.
        /// </summary>
        public const string Instances = "external-apps/instances";

        public const string InstanceById = "external-apps/instances/{instanceId}";

        /// <summary>
        ///     What the Update dialog needs before it asks for anything.
        /// </summary>
        /// <remarks>
        ///     The target manifest version and its sha256, every target variable with the current values masked, the
        ///     permissions this update ADDS, the resource verdict, and whether the update can proceed at all.
        /// </remarks>
        public const string InstanceUpdatePreview = "external-apps/instances/{instanceId}/update-preview";

        // The five lifecycle verbs. Each commits an intent and answers 202 with the ADMITTED snapshot — the row as the synchronous admission left it, taken before the
        // operation runner starts — which is why the status set carries Starting, Stopping, Updating and Resetting. All five carry `expectedVersion`.
        public const string InstanceStart = "external-apps/instances/{instanceId}/start";

        public const string InstanceStop = "external-apps/instances/{instanceId}/stop";

        public const string InstanceRestart = "external-apps/instances/{instanceId}/restart";

        public const string InstanceReset = "external-apps/instances/{instanceId}/reset";

        public const string InstanceUpdate = "external-apps/instances/{instanceId}/update";

        /// <summary>Cancels the in-flight operation on this instance; it settles to <c>Failed</c> with its storage kept. 409 when nothing is running.</summary>
        public const string InstanceCancel = "external-apps/instances/{instanceId}/cancel";

        /// <summary>Reconfigure a STOPPED instance. Secret values are masked on the way out and kept on the sentinel on the way in.</summary>
        public const string InstanceVariables = "external-apps/instances/{instanceId}/variables";

        /// <summary>The append-only event log in ASCENDING sequence order, paged by an EXCLUSIVE <c>?afterSequence=</c> lower bound; the same <c>ListEventsAsync</c> the hub replays from.</summary>
        public const string InstanceEvents = "external-apps/instances/{instanceId}/events";

        /// <summary>
        ///     Bounded container logs read from the daemon, nothing persisted: <c>?service=</c> and <c>?tail=</c>.
        /// </summary>
        /// <remarks>
        ///     A tail above 2000 is REJECTED, never clamped — a silently clamped <c>tail=100000</c> reads as a truncated
        ///     log. The text is raw application output and is NOT masked.
        /// </remarks>
        public const string InstanceLogs = "external-apps/instances/{instanceId}/logs";

        /// <summary>SignalR notification hub. Full path (mapped via MapHub, not the FastEndpoints prefix).</summary>
        public const string Hub = "/api/local/v1/external-apps/hub";
    }
}
