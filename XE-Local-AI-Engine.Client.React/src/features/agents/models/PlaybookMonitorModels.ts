import { z } from "zod";

// Playbook P5 — read-only cohort-monitoring view for an agent's Enabled playbook actions plus the relevance-
// retrieval config. This is a pure analytics read over feedback already persisted node-locally (the windowed
// down-vote query keyed on EnabledAtUtc). NO mutations, no AI generation: it only surfaces, per Enabled action,
// the before/after down-vote rates and a derived verdict so an operator can flag a dead/harmful action for
// review (the signal is coarse and agent-level — flag-only, never auto-disable).
//
// The `status` is one of exactly four wire strings; `flagged` is the operator-review affordance (true only when
// the action has enough after-enable samples and did not improve). `facetToolName` is the optional tool scope the
// action declares (null when the action is not tool-scoped). `retrieval` carries the relevance-gate config
// (threshold + topK) so the panel can render the "injection is relevance-gated" banner with live numbers. The
// boundary is validated with Zod `safeParse` (→ null on garbage), mirroring FeedbackInsightsModels.

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

// Relevance-retrieval config the panel surfaces in the "injection is relevance-gated" banner. `threshold` is the
// Enabled-action count past which per-turn retrieval kicks in; `topK` is how many actions are injected per turn.
export interface PlaybookRetrievalConfig {
	readonly threshold: number;
	readonly topK: number;
}

export interface PlaybookMonitor {
	readonly items: readonly PlaybookMonitorItem[];
	readonly retrieval: PlaybookRetrievalConfig;
}

const statusSchema = z.enum(["Improved", "Flat", "Regressed", "InsufficientData"]);

const monitorItemSchema = z.object({
	actionId: z.string(),
	enabledAtUtc: z.number(),
	beforeDownRate: z.number(),
	afterDownRate: z.number(),
	afterSampleSize: z.number(),
	status: statusSchema,
	flagged: z.boolean(),
	// Nullable on the wire — null when the action declares no tool scope.
	facetToolName: z.string().nullable(),
});

const retrievalSchema = z.object({
	threshold: z.number(),
	topK: z.number(),
});

// Boundary schema for the GET /agents/{id}/playbook/monitor response.
export const playbookMonitorSchema = z.object({
	items: z.array(monitorItemSchema),
	retrieval: retrievalSchema,
});

// The validated wire shape (camelCase, matches the agents surface). Kept separate from the readonly domain
// view-model so the boundary owns parsing and the rest of the feature consumes the immutable shape.
export type PlaybookMonitorDto = z.infer<typeof playbookMonitorSchema>;

// Validate + deserialize the wire payload at the boundary with safeParse. A malformed payload throws a
// descriptive error (caught by the TanStack Query error path) rather than rendering partial/wrong monitoring.
export function toPlaybookMonitor(payload: unknown): PlaybookMonitor {
	const parsed = playbookMonitorSchema.safeParse(payload);
	if (!parsed.success) {
		throw new Error(`Invalid playbook monitor payload: ${parsed.error.message}`);
	}

	const dto = parsed.data;
	return {
		items: dto.items.map((item) => ({ ...item })),
		retrieval: { ...dto.retrieval },
	};
}
