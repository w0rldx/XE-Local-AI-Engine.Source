namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>Local API contract type for model-fit, the hardware-aware local model advisor.</summary>
    /// <remarks>
    ///     Cache-first: the latest endpoint reads the cached recommendation snapshot and never runs the advisor, and the
    ///     refresh endpoint delegates to the scheduler trigger rather than executing the advisor directly. The advisor
    ///     management routes are thin transport over the llama.cpp binary/supervisor seams and the Hugging Face GGUF
    ///     discovery/store/token seams. There is no approved-image concept or provider-name param, and benchmark stays gated.
    /// </remarks>
    public static class ModelFit
    {
        // Latest cached recommendation snapshot (query-filtered by useCase). The literal "latest" segment follows
        // "recommendations", so it never collides with the "refresh" action below.
        public const string RecommendationsLatest = "model-fit/recommendations/latest";

        // Manual refresh trigger — a template-guarded facade over the scheduler trigger service. The literal "refresh"
        // segment follows "recommendations", so it never collides with "latest".
        public const string RecommendationsRefresh = "model-fit/recommendations/refresh";

        // Sanitized hardware profile (RAM/VRAM/GPU vendor/CPU/disk aggregates only — no machine identifiers).
        // IHardwareProfiler passthrough.
        public const string HardwareProfile = "model-fit/hardware-profile";

        // Live whole-machine RAM and per-GPU VRAM usage for the top-bar gauge (ILiveMemorySampler), aggregates only.
        public const string Resources = "model-fit/resources";

        // The image and whisper daemons held in memory (in-memory supervisor state + activity gates); llama.cpp stays on Running.
        public const string RuntimeResidents = "model-fit/runtime-residents";

        // GGUF repo discovery (IHuggingFaceGgufDiscovery search). The literal "browse" segment keeps it distinct.
        public const string GgufBrowse = "model-fit/gguf/browse";

        // Per-repo GGUF file inspection (IHuggingFaceGgufDiscovery inspect): the selectable quants + sizes for one
        // repo, so the browse UI can offer a quant picker. The literal "inspect" segment keeps it distinct.
        public const string GgufInspect = "model-fit/gguf/inspect";

        // Download a chosen GGUF file (IGgufModelStore) — starts a background, cancellable download keyed by
        // model name; the cancel action signals the in-flight download's token.
        public const string Download = "model-fit/download";
        public const string DownloadCancel = "model-fit/download/cancel";

        // Progress polling for in-flight and recently-finished GGUF downloads (IGgufDownloadCoordinator status registry). List returns all tracked statuses; the {modelName}
        // variant returns one (404 when unknown). The list endpoint is the one-shot hydrate on mount; live progress streams over the DownloadHub below instead of a poll.
        public const string Downloads = "model-fit/gguf/downloads";
        public const string DownloadStatus = "model-fit/gguf/downloads/{modelName}";
        public const string DownloadOperationStatus = "model-fit/gguf/downloads/operations/{operationId:guid}";

        // SignalR push hub for GGUF download status changes. Full path (mapped via MapHub, not the FastEndpoints prefix),
        // mirroring the other local hubs. Replaces the per-second downloads poll; each push carries the sanitized status.
        public const string DownloadHub = "/api/local/v1/model-fit/gguf/downloads/hub";

        public const string ImportCapability = "model-fit/gguf/import/capability";
        public const string ImportPreview = "model-fit/gguf/import/preview";
        public const string Import = "model-fit/gguf/import";
        public const string Imports = "model-fit/gguf/imports";
        public const string ImportStatus = "model-fit/gguf/imports/{operationId:guid}";
        public const string ImportCancel = "model-fit/gguf/imports/{operationId:guid}/cancel";

        // Running llama-server processes derived from the supervisor health snapshot; eject tree-kills one.
        public const string Running = "model-fit/running";
        public const string RunningEject = "model-fit/running/eject";

        // Curated model catalog: read-only metadata (version/source/fetchedAt) and an operator forced-refresh trigger. The catalog content itself rides the existing
        // recommendations/latest response (section/tier fields on each row) — these two routes are catalog-provenance only.
        public const string CatalogInfo = "model-fit/catalog";
        public const string CatalogRefresh = "model-fit/catalog/refresh";

        // Resolved/pinned llama.cpp binary version (ILlamaCppBinaryManager). GET reads the pinned-tag + resolved
        // variant; POST ensures the binary for a chosen variant is present (download + hash-verify).
        public const string LlamaCppVersion = "model-fit/llamacpp/version";

        // Read-only dynamic-runtime status (ILlamaCppUpdateState + IInstalledRuntimeStore): installed vs recommended
        // (+ dev-mode upstream-latest) and whether a newer recommended runtime is available. Never triggers a download.
        public const string LlamaCppRuntime = "model-fit/llamacpp/runtime";

        // Operator-initiated install/update of a chosen llama.cpp release tag (ILlamaCppBinaryManager.InstallTagAsync via
        // the release catalog). Validates the tag format before resolving the asset + digest and installing.
        public const string LlamaCppUpdate = "model-fit/llamacpp/update";

        // Restores automatic variant selection after an explicit install pinned one (ILlamaCppBinaryManager.ClearVariantPinAsync). Rewrites the record only; takes
        // effect on the next spawn and leaves a recorded source build alone.
        public const string LlamaCppVariantAutomatic = "model-fit/llamacpp/variant/automatic";

        // In-app Linux source build of llama.cpp for a chosen backend, because the pins carry no Linux CUDA prebuilt. Admission gating, the prerequisites checklist and the
        // phase/log payload: docs/wiki/09-api-and-hubs.md ("Design notes on the newer endpoint families"). The literal "source-build" segments follow "llamacpp", and the hub is a full MapHub path.
        public const string SourceBuildPrerequisites = "model-fit/llamacpp/source-build/prerequisites";
        public const string SourceBuild = "model-fit/llamacpp/source-build";
        public const string SourceBuildStatus = "model-fit/llamacpp/source-build/status";
        public const string SourceBuildCancel = "model-fit/llamacpp/source-build/cancel";
        public const string SourceBuildRemove = "model-fit/llamacpp/source-build/remove";
        public const string SourceBuildHub = "/api/local/v1/model-fit/llamacpp/source-build/hub";

        // Read-only first-run runtime-acquisition snapshot (IRuntimeAcquisitionStatusRegistry) — docs/wiki/09-api-and-hubs.md ("Design notes on the newer endpoint families").
        // It NEVER triggers an acquisition, unlike the ensure POST on LlamaCppVersion. The literal "acquisition" segment follows "llamacpp", so it collides with none of the routes above.
        public const string LlamaCppAcquisition = "model-fit/llamacpp/acquisition";

        // SignalR push hub for runtime acquisition progress: a full path (mapped via MapHub, not the FastEndpoints prefix), mirroring the other local hubs. Each push carries
        // the same sanitized payload the hydrate GET serves, stamped with the monotonic sequence the client reconciles hydrate and push by.
        public const string LlamaCppAcquisitionHub = "/api/local/v1/model-fit/llamacpp/acquisition/hub";

        // SignalR push hub ticking when runtime residency changes, so the header refetches Running and RuntimeResidents instead of polling them. A full MapHub path.
        public const string ResidencyHub = "/api/local/v1/model-fit/residency/hub";

        // HF access-token set/clear (IHfTokenStore). The endpoint NEVER returns the token; GET reports presence
        // only (security gate).
        public const string HfToken = "model-fit/hf-token";

        // Inference Optimizer profile surface (IInferenceProfileService). The four POST actions each carry their target in the body, never a route param, so the POST always has
        // a body and sidesteps the FastEndpoints 415-on-bodyless-POST issue. See docs/wiki/09-api-and-hubs.md ("Design notes on the newer endpoint families").
        public const string Profiles = "model-fit/profiles";
        public const string ProfilesExplore = "model-fit/profiles/explore";
        public const string ProfilesBenchmark = "model-fit/profiles/benchmark";
        public const string ProfilesFreeze = "model-fit/profiles/freeze";
        public const string ProfilesInvalidate = "model-fit/profiles/invalidate";
    }
}
