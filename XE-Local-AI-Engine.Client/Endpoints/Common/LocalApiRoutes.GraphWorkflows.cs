namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     Graph workflows: operator-authored definitions and the runs started from them.
    /// </summary>
    /// <remarks>
    ///     The whole surface is gated on <c>GraphWorkflows:Enabled</c> by request-path middleware in <c>Program</c>
    ///     that answers 404 for anything under <see cref="Root" />, which is why the prefix is a constant rather than
    ///     spelled at each route. The run, event, decision and hub paths sit under that same root, so the gate covers
    ///     them too.
    /// </remarks>
    public static class GraphWorkflows
    {
        public const string Root = "graph-workflows";

        /// <summary>
        ///     The one route carved out of the disabled-node 404 sweep, mirroring <see cref="DevelopmentWorkflows.Capability" />:
        ///     it answers <c>enabled: false</c> so the SPA hides the feature and keeps chat sending instead of reading the
        ///     bodyless 404 as a load failure.
        /// </summary>
        public const string Capability = "graph-workflows/capability";

        /// <summary>The definition collection: GET lists without the graph blob, POST validates the graph and stores it.</summary>
        public const string Definitions = "graph-workflows/definitions";

        /// <summary>GET / PUT with the version it was edited from / DELETE, which 409s while a live run pins the definition.</summary>
        public const string DefinitionById = "graph-workflows/definitions/{definitionId}";

        /// <summary>
        ///     Validation without saving: the editor asks the RUNTIME's own parser whether a graph would route, so the
        ///     answer it draws is the answer a run would get rather than a second implementation of the same rules.
        /// </summary>
        public const string DefinitionsValidate = "graph-workflows/definitions/validate";

        /// <summary>GET one definition as the portable share file: format, format version, name, description and graph.</summary>
        public const string DefinitionExport = "graph-workflows/definitions/{definitionId}/export";

        /// <summary>POST a share file to create a definition from it, through the same parser and tool gate as create.</summary>
        public const string DefinitionsImport = "graph-workflows/definitions/import";

        /// <summary>
        ///     The Tool node's picker feed: every tool a Tool node may actually run, already filtered server-side to
        ///     the invocation envelope by the same service the runtime invokes through, so the picker cannot offer a name the
        ///     run would then refuse.
        /// </summary>
        public const string Tools = "graph-workflows/tools";

        /// <summary>
        ///     Starts a run of one definition, answering 202 with the run id.
        /// </summary>
        /// <remarks>
        ///     The endpoint commits a durable intent and the dispatcher advances it out of band, so the run legitimately
        ///     reads <c>Pending</c> when the answer lands. The caller's <c>requestId</c> is the idempotency key — the
        ///     same one always answers with the same run.
        /// </remarks>
        public const string DefinitionRuns = "graph-workflows/definitions/{definitionId}/runs";

        /// <summary>The run list, newest first.</summary>
        public const string Runs = "graph-workflows/runs";

        /// <summary>One run with its node-run summaries. No documents: those are a per-node read.</summary>
        public const string RunById = "graph-workflows/runs/{runId}";

        /// <summary>Requests a cancel. 202 like the start — live node runs drain first, so the run reads <c>Cancelling</c>.</summary>
        public const string RunCancel = "graph-workflows/runs/{runId}/cancel";

        /// <summary>One node run in full, input and output documents included. Keyed by node key, which is its identity within the run.</summary>
        public const string RunNodeByKey = "graph-workflows/runs/{runId}/nodes/{nodeKey}";

        /// <summary>
        ///     Answers the pause at <c>nodeKey</c>.
        /// </summary>
        /// <remarks>
        ///     The caller's <c>operationId</c> is the idempotency key — the same one always answers with the decision
        ///     it already recorded, and a DIFFERENT one on an answered pause is a second human act, refused with the
        ///     decision that stands on the body.
        /// </remarks>
        public const string RunNodeDecide = "graph-workflows/runs/{runId}/nodes/{nodeKey}/decide";

        /// <summary>
        ///     Steers the queued or running Agent/LLM call node at <c>nodeKey</c> of a chat-bound run: 202, and the
        ///     dispatcher re-runs the node on the same attempt. <c>operationId</c> is the idempotency key.
        /// </summary>
        public const string RunNodeSteer = "graph-workflows/runs/{runId}/nodes/{nodeKey}/steer";

        /// <summary>
        ///     The run's event log, paged from an EXCLUSIVE watermark and capped at the configured replay limit, which
        ///     the response reports rather than leaving a client to infer from a full page.
        /// </summary>
        public const string RunEvents = "graph-workflows/runs/{runId}/events";

        /// <summary>
        ///     A chat message into workflow mode: starts a run bound to the conversation, or answers its parked ChatInput.
        ///     202; the caller's <c>requestId</c> is the idempotency key.
        /// </summary>
        public const string ConversationMessages = "graph-workflows/conversations/{conversationId}/messages";

        /// <summary>The runs bound to one conversation, newest first — what puts the chat page back into workflow mode on a reload.</summary>
        public const string ConversationRuns = "graph-workflows/conversations/{conversationId}/runs";

        /// <summary>
        ///     Resolves a spoken or typed command to one workflow open to <c>Intent</c> starts and starts it. 200 for
        ///     <c>Started</c> and <c>NotMatched</c>; 409 <c>GraphWorkflowIntentExpired</c> for a stale utterance.
        /// </summary>
        public const string IntentsInvoke = "graph-workflows/intents/invoke";

        /// <summary>SignalR notification hub. Full path (mapped via MapHub, not the FastEndpoints prefix).</summary>
        public const string Hub = "/api/local/v1/graph-workflows/hub";
    }
}
