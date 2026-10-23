namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     The AgentHome run history. Runs live only as directories on disk, so this family is a bounded scan of them
    ///     rather than a query over rows, and the run id in a path is a directory name every route re-gates before it
    ///     composes anything.
    /// </summary>
    public static class AgentHomeRuns
    {
        /// <summary>Newest-first page of run summaries (GET, limit/offset), with the unpaged total.</summary>
        public const string List = "agent-home/runs";

        /// <summary>Removes one run's directory (DELETE, no body). Refused while a run holds the execution lease.</summary>
        public const string ById = "agent-home/runs/{runId}";

        /// <summary>The run's own event log as capped text (GET), with whether a middle was left out.</summary>
        public const string Log = "agent-home/runs/{runId}/log";

        /// <summary>
        ///     The run's exported <c>changes.patch</c> as capped text (GET), for reading only — landing it is the
        ///     <see cref="AgentHomePatch" /> family, which re-validates everything this route merely shows.
        /// </summary>
        public const string PatchText = "agent-home/runs/{runId}/patch";
    }
}
