namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    public static class Scheduler
    {
        // The family root FeatureSwitchMiddleware gates: every scheduler route, hub included, answers 404 while the switch is off.
        public const string Root = "scheduler";

        // Flat template catalog. Kept separate from job-id routes so templates cannot be parsed as ids.
        public const string Templates = "scheduler/templates";

        // Job collection (GET list, POST create) and individual job resource (GET, PUT, DELETE).
        public const string Jobs = "scheduler/jobs";
        public const string JobById = "scheduler/jobs/{scheduledJobId}";

        // Lifecycle actions use literal segments after the job id, keeping action names distinct from JobById.
        public const string JobEnable = "scheduler/jobs/{scheduledJobId}/enable";
        public const string JobDisable = "scheduler/jobs/{scheduledJobId}/disable";
        public const string JobTrigger = "scheduler/jobs/{scheduledJobId}/trigger";

        // Run history uses a flat query-filtered collection plus an individual run resource.
        public const string Runs = "scheduler/runs";
        public const string RunById = "scheduler/runs/{runId}";

        // Cancellation is run-scoped rather than job-scoped; the management service maps it to a Quartz interrupt.
        public const string RunCancel = "scheduler/runs/{runId}/cancel";

        // SignalR push hub for scheduler lifecycle events. Full path (mapped via MapHub, not the FastEndpoints prefix),
        // mirroring LocalChat.Hub.
        public const string Hub = "/api/local/v1/scheduler/hub";
    }
}
