namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     Local image-generation routes (jobs create/list/get/cancel, encrypted PNG retrieve, installed models) and the
    ///     progress hub path. Jobs persist; the coordinator serializes generation to one running job at a time.
    /// </summary>
    public static class Images
    {
        // Job collection (POST create, GET list) and individual job resource (GET status).
        public const string Jobs = "images/jobs";
        public const string JobById = "images/jobs/{jobId}";

        // Cancel is job-scoped; the coordinator picks clean-cancel (queued) vs kill+restart (generating) internally. The
        // literal "cancel" segment follows the job id, keeping it distinct from JobById.
        public const string JobCancel = "images/jobs/{jobId}/cancel";

        // Decrypted image retrieve (generated PNG or uploaded PNG/JPEG). {imageId} is the server-generated image id (never a client-supplied path).
        public const string ImageById = "images/{imageId}";

        // Operator-uploaded edit sources: POST one multipart image, GET the list (newest first), DELETE one by id. The literal "uploads" segment outranks ImageById's
        // {imageId} parameter in routing, so GET images/uploads never binds as an image id.
        public const string Uploads = "images/uploads";
        public const string UploadById = "images/uploads/{imageId}";

        // AI-assisted prompt drafting with a node-local chat model. Writes nothing — the draft only fills the form.
        public const string PromptDraft = "images/prompts/draft";

        // Installed image-model registry (GET list).
        public const string Models = "images/models";

        // Image-model weight downloads: POST starts a detached file-set pull, GET lists every tracked download's phase
        // (Running/Completed/Cancelled/Failed) so a failure is observable instead of silent.
        public const string ModelDownloads = "images/models/downloads";

        // Cancels an in-flight file-set pull. An image model can be tens of gigabytes, so a mis-started download that
        // could not be stopped would hold the node's bandwidth and disk until it finished.
        public const string ModelDownloadCancel = "images/models/downloads/cancel";

        // Curated image-model catalog: the one-click install list, annotated with this machine's hardware fit and whether each entry is already installed. The literal "catalog"
        // segment precedes nothing, so it cannot be captured by ModelByName's {modelName} route (that one is DELETE-only in any case).
        public const string ModelCatalog = "images/models/catalog";

        // Hugging Face image-model repo discovery (IImageModelDiscovery search) and per-repo weight-file inspection —
        // the browse → inspect → pick pipeline that replaces hand-typing a repo id and a file name.
        public const string ModelBrowse = "images/models/browse";
        public const string ModelInspect = "images/models/inspect";

        // Removes an installed model's weights and registry entry. Without it a node that has installed several
        // multi-gigabyte file-sets has no in-app way to reclaim the disk.
        public const string ModelByName = "images/models/{modelName}";

        // Managed stable-diffusion.cpp runtime and Linux source-build orchestration.
        public const string Runtime = "images/runtime";
        public const string RuntimeEject = "images/runtime/eject";
        public const string RuntimeSourceBuild = "images/runtime/source-build";
        public const string RuntimeSourceBuildPrerequisites = "images/runtime/source-build/prerequisites";
        public const string RuntimeSourceBuildStatus = "images/runtime/source-build/status";
        public const string RuntimeSourceBuildCancel = "images/runtime/source-build/cancel";
        public const string RuntimeSourceBuildRemove = "images/runtime/source-build/remove";

        // SignalR push hub for image-job progress. Full path (mapped via MapHub, not the FastEndpoints prefix),
        // mirroring the other local hubs. Each push carries the coarse status + seq.
        public const string Hub = "/api/local/v1/images/hub";

        // SignalR push hub for stable-diffusion.cpp source-build phase and log changes.
        public const string RuntimeSourceBuildHub = "/api/local/v1/images/runtime/source-build/hub";
    }
}
