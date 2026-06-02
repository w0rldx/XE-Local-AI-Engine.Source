// Playbook P5 — read-only cohort-monitoring view for an agent's Enabled playbook actions plus the relevance-
// retrieval config. This is a pure analytics read over feedback already persisted node-locally (the windowed
// down-vote query keyed on EnabledAtUtc). NO mutations, no AI generation: it only surfaces, per Enabled action,
// the before/after down-vote rates and a derived verdict so an operator can flag a dead/harmful action for
// review (the signal is coarse and agent-level — flag-only, never auto-disable).
//
// The `status` is one of exactly four wire strings; `flagged` is the operator-review affordance (true only when
// the action has enough after-enable samples and did not improve). `facetToolName` is the optional tool scope the
// action declares (null when the action is not tool-scoped). `retrieval` carries the relevance-gate config
// (threshold + topK) so the panel can render the "injection is relevance-gated" banner with live numbers.
// Boundary validation is owned by the generated hey-api zod validator + the withResponseValidation bridge;
// PlaybookMonitorMappers projects the validated wire shape into these immutable domain view-models.

// The four monitor verdicts. InsufficientData = too few after-enable samples (never flagged); Improved = the
// after-enable down-rate fell below the before-enable rate; Regressed = it rose above; Flat = within the epsilon.
export type PlaybookMonitorStatus = "Improved" | "Flat" | "Regressed" | "InsufficientData";

// Domain view-model for one Enabled action's monitoring signal. Timestamps are epoch milliseconds (long on the
// wire); down-rates are fractions in [0,1]. Joined to a panel row by `actionId`.
export interface PlaybookMonitorItem {
	readonly actionId: string;
	readonly enabledAtUtc: number;
	readonly beforeDownRate: number;
	readonly afterDownRate: number;
	readonly afterSampleSize: number;
	readonly status: PlaybookMonitorStatus;
	readonly flagged: boolean;
	// The tool scope the action is monitored against (null when the action is not tool-scoped).
	readonly facetToolName: string | null;
}

// Which ranker the node uses to pick the top-K actions once retrieval is gated. "embedding" = cosine over node-
// local embeddings (active only when the operator configured an embedding model); "lexical" = token-overlap (the
// effective default and the auto-fallback when embeddings are off/unreachable). The active ranker is decided
// purely by whether the node configured an embedding model.
export type PlaybookRetrievalRanker = "embedding" | "lexical";

// Relevance-retrieval config the panel surfaces in the "injection is relevance-gated" banner. `threshold` is the
// Enabled-action count past which per-turn retrieval kicks in; `topK` is how many actions are injected per turn.
// `ranker` names the active selection strategy; `embeddingModel` is the configured embedding model name, present
// only when `ranker === "embedding"` (null/absent for lexical).
export interface PlaybookRetrievalConfig {
	readonly threshold: number;
	readonly topK: number;
	readonly ranker: PlaybookRetrievalRanker;
	readonly embeddingModel: string | null;
}

export interface PlaybookMonitor {
	readonly items: readonly PlaybookMonitorItem[];
	readonly retrieval: PlaybookRetrievalConfig;
}
