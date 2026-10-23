namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     Agent work sessions: the objective-scoped runs an operator starts, their lifecycle verbs, the five
    ///     sequence-filtered feeds the session view pages through, and the live-notification hub.
    /// </summary>
    /// <remarks>
    ///     Approvals and <c>ask_user</c> answers deliberately have no route here — a session view embeds the
    ///     conversation the session owns and resolves both through the existing chat routes, so there is one approval
    ///     path on the node rather than two that can drift.
    /// </remarks>
    public static class WorkSessions
    {
        public const string Root = "work-sessions";

        /// <summary>
        ///     The one route carved out of the disabled-node 404 sweep, mirroring <see cref="Development.Capability" />.
        /// </summary>
        /// <remarks>
        ///     It answers <c>enabled: false</c> so the SPA can say "switched off on this node" instead of rendering the
        ///     bodyless 404 as a load failure. A literal segment outranks <see cref="ById" />'s parameter, so this can
        ///     never be read as a session id.
        /// </remarks>
        public const string Capability = "work-sessions/capability";

        public const string ById = "work-sessions/{sessionId}";
        public const string Start = "work-sessions/{sessionId}/start";
        public const string Pause = "work-sessions/{sessionId}/pause";
        public const string Resume = "work-sessions/{sessionId}/resume";
        public const string Cancel = "work-sessions/{sessionId}/cancel";

        // The five feeds. Each takes an exclusive sinceSeq query parameter, so the client re-reads only what the hub
        // said had changed; only the event feed pages, because it is the only one that grows without bound.
        public const string Tasks = "work-sessions/{sessionId}/tasks";
        public const string Findings = "work-sessions/{sessionId}/findings";
        public const string Artifacts = "work-sessions/{sessionId}/artifacts";
        public const string Checkpoints = "work-sessions/{sessionId}/checkpoints";
        public const string Events = "work-sessions/{sessionId}/events";
        public const string ArtifactContent = "work-sessions/{sessionId}/artifacts/{artifactId}/content";

        // A user follow-up into the session's owned conversation. Not the chat hub's SendMessage: this one persists a
        // standalone user row and hands the turn to the supervisor rather than streaming an assistant reply.
        public const string Messages = "work-sessions/{sessionId}/messages";

        // SignalR notification hub. Full path (mapped via MapHub, not the FastEndpoints prefix), mirroring the others.
        public const string Hub = "/api/local/v1/work-sessions/hub";
    }
}
