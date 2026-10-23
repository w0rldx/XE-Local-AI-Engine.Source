namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     Development workflows: work items, definitions, runs and their node runs.
    /// </summary>
    /// <remarks>
    ///     The whole surface is gated on <c>DevWorkflows:Enabled</c> by request-path middleware in <c>Program</c> that
    ///     answers 404 for anything under <see cref="Root" />, which is why the prefix is a constant rather than
    ///     spelled at each route. The literal first segment cannot be captured by any <c>development/…</c> route
    ///     parameter — the two families diverge at segment one.
    /// </remarks>
    public static class DevelopmentWorkflows
    {
        public const string Root = "development-workflows";

        /// <summary>
        ///     The one route carved out of the disabled-node 404 sweep, mirroring <see cref="Development.Capability" />:
        ///     it answers <c>enabled: false</c> so the SPA can say "switched off on this node" instead of rendering the
        ///     bodyless 404 as a load failure.
        /// </summary>
        public const string Capability = "development-workflows/capability";

        /// <summary>The work-item collection. <c>?status=</c> filters on the status the RUNTIME writes, never a client.</summary>
        public const string WorkItems = "development-workflows/work-items";

        public const string WorkItemById = "development-workflows/work-items/{workItemId}";

        /// <summary>
        ///     Run start, nested under the work item that owns it. The definition rides in the body rather than the
        ///     path because it is a per-run choice: one work item is re-runnable against a revised definition later.
        /// </summary>
        public const string WorkItemRuns = "development-workflows/work-items/{workItemId}/runs";

        /// <summary>The definition collection. <c>?includeArchived=</c> shows the ones DELETE archived.</summary>
        public const string Definitions = "development-workflows/definitions";

        public const string DefinitionById = "development-workflows/definitions/{definitionId}";

        /// <summary>
        ///     The rule-set collection. No filter: the resolver's own working set is "every enabled one", and the
        ///     management page draws the disabled ones beside them.
        /// </summary>
        public const string RuleSets = "development-workflows/rule-sets";

        /// <summary>DELETE here is a HARD delete, unlike a definition's: a node run recorded what applied, so nothing dangles.</summary>
        public const string RuleSetById = "development-workflows/rule-sets/{ruleSetId}";

        /// <summary>The run list, filtered by <c>?workItemId=</c>, <c>?status=</c> and <c>?limit=</c>.</summary>
        public const string Runs = "development-workflows/runs";

        /// <summary>
        ///     One run in full: its pinned graph, every node-run summary and the counters. This is THE fetch that
        ///     repaints the whole view, which is why there is deliberately no node-run list route — no node card ever
        ///     needs a second request.
        /// </summary>
        public const string RunById = "development-workflows/runs/{runId}";

        // The three lifecycle verbs. Each commits an intent and answers 202: the transition completes out of band on
        // the dispatcher's clock, which is exactly why the run status set carries Pausing and Cancelling.
        public const string RunPause = "development-workflows/runs/{runId}/pause";
        public const string RunResume = "development-workflows/runs/{runId}/resume";
        public const string RunCancel = "development-workflows/runs/{runId}/cancel";

        /// <summary>
        ///     The append-only event log, paged by an EXCLUSIVE <c>?sinceSeq=</c> lower bound.
        /// </summary>
        /// <remarks>
        ///     Sequences are strictly increasing but NOT contiguous — the run's counter is shared with node-runs and
        ///     artifacts — so a client follows the watermark rather than counting rows.
        /// </remarks>
        public const string RunEvents = "development-workflows/runs/{runId}/events";

        /// <summary>The heavier per-node drill-down: session and task ids, artifacts, applied rule sets, decisions.</summary>
        public const string NodeRunById = "development-workflows/runs/{runId}/nodes/{nodeRunId}";

        /// <summary>
        ///     The ONE decision surface. A gate's answer and a stuck node-run's intervention are the same human act, so
        ///     <c>Retry</c>, <c>Skip</c> and <c>Abandon</c> travel this route too — there is no separate retry endpoint.
        /// </summary>
        public const string NodeRunDecision = "development-workflows/runs/{runId}/nodes/{nodeRunId}/decision";

        /// <summary>
        ///     The artifact feed, <c>?sinceSeq=</c>-capable because artifact rows are append-only. Staleness flips do
        ///     NOT advance this cursor: they mutate rows without re-stamping a sequence, so they are announced on the
        ///     event feed and observed by refetching.
        /// </summary>
        public const string RunArtifacts = "development-workflows/runs/{runId}/artifacts";

        public const string RunArtifactContent = "development-workflows/runs/{runId}/artifacts/{artifactId}/content";

        /// <summary>SignalR notification hub. Full path (mapped via MapHub, not the FastEndpoints prefix).</summary>
        public const string Hub = "/api/local/v1/development-workflows/hub";
    }
}
