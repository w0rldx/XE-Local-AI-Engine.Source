import { z } from "zod";

// Playbook P4 — per-agent golden conversation set. A golden case is an operator-authored input conversation plus
// the expected-good signal used to score a candidate playbook action: a deterministic assertion (required /
// forbidden phrases) and/or a judge rubric (≥1 of the two is required). The eval runner replays the case against
// the baseline and candidate prompts and gates promotion on no-regression. Free text is encrypted at rest on the
// node; the wire shape here is the decrypted camelCase view. The boundary is validated with Zod `safeParse` so a
// malformed payload surfaces as a thrown error rather than a silently-wrong panel.

// One turn of the input conversation up to the eval point.
export interface GoldenTurn {
	readonly role: string;
	readonly text: string;
}

// Deterministic scoring signal: the candidate output must contain every required phrase and none of the
// forbidden phrases. When present, scoring is deterministic (no model call).
export interface GoldenAssertion {
	readonly requiredPhrases: readonly string[];
	readonly forbiddenPhrases: readonly string[];
}

// Domain view-model for a golden case. Timestamps are epoch milliseconds (long on the wire). `assertion`/`rubric`
// are nullable (≥1 of the two is present); `enabled` lets an operator park a case without deleting it.
export interface GoldenConversation {
	readonly id: string;
	readonly agentDefinitionId: string;
	readonly title: string;
	readonly inputTurns: readonly GoldenTurn[];
	readonly assertion: GoldenAssertion | null;
	readonly rubric: string | null;
	readonly enabled: boolean;
	readonly createdAtUtc: number;
	readonly updatedAtUtc: number;
}

// Request body for creating a golden case. Identity/timestamps are server-managed; `enabled` defaults server-side
// when omitted. `assertion`/`rubric` are optional but the backend rejects a case with neither.
export interface CreateGoldenConversationRequestDto {
	title: string;
	inputTurns: GoldenTurn[];
	assertion?: GoldenAssertion;
	rubric?: string;
	enabled?: boolean;
}

// Boundary length caps mirroring the backend GoldenConversationService — the title is a short label, the serialized
// turns can be larger (multi-turn conversations), and the rubric/serialized phrases hold scoring text. The turns and
// phrases are capped on their serialized JSON length (what the backend stores/encrypts), matching the server check.
export const GOLDEN_TITLE_MAX = 200;
export const GOLDEN_INPUT_TURNS_MAX = 50_000;
export const GOLDEN_RUBRIC_MAX = 20_000;
export const GOLDEN_ASSERTION_MAX = 20_000;

const goldenTurnSchema = z.object({
	role: z.string(),
	text: z.string(),
});

const goldenAssertionSchema = z.object({
	requiredPhrases: z.array(z.string()),
	forbiddenPhrases: z.array(z.string()),
});

// Boundary schema for a single golden case on the wire. `assertion`/`rubric` are nullable; an absent/garbage
// assertion degrades to null rather than failing the whole parse.
const goldenConversationSchema = z.object({
	id: z.string(),
	agentDefinitionId: z.string(),
	title: z.string(),
	inputTurns: z.array(goldenTurnSchema),
	assertion: goldenAssertionSchema.nullish(),
	rubric: z.string().nullish(),
	enabled: z.boolean(),
	createdAtUtc: z.number(),
	updatedAtUtc: z.number(),
});

// Boundary schema for the GET /agents/{id}/golden-conversations response envelope.
export const listGoldenConversationsResponseSchema = z.object({
	items: z.array(goldenConversationSchema),
});

// The validated wire shapes (camelCase). Kept separate from the readonly domain view-models so the boundary owns
// parsing and the rest of the feature consumes the immutable shape.
export type GoldenConversationDto = z.infer<typeof goldenConversationSchema>;

// Validate + deserialize a single golden case at the boundary. Normalizes the nullish assertion/rubric to a
// strict null so the panel never branches on undefined.
export function toGoldenConversation(dto: GoldenConversationDto): GoldenConversation {
	return {
		id: dto.id,
		agentDefinitionId: dto.agentDefinitionId,
		title: dto.title,
		inputTurns: dto.inputTurns.map((turn) => ({ ...turn })),
		assertion: dto.assertion
			? {
					requiredPhrases: [...dto.assertion.requiredPhrases],
					forbiddenPhrases: [...dto.assertion.forbiddenPhrases],
				}
			: null,
		rubric: dto.rubric ?? null,
		enabled: dto.enabled,
		createdAtUtc: dto.createdAtUtc,
		updatedAtUtc: dto.updatedAtUtc,
	};
}

// Validate + deserialize the list envelope at the boundary with safeParse. A malformed payload throws a
// descriptive error (caught by the TanStack Query error path) rather than rendering partial/wrong data.
export function toGoldenConversations(payload: unknown): GoldenConversation[] {
	const parsed = listGoldenConversationsResponseSchema.safeParse(payload);
	if (!parsed.success) {
		throw new Error(`Invalid golden conversations payload: ${parsed.error.message}`);
	}
	return parsed.data.items.map(toGoldenConversation);
}

// Validate + deserialize a single created golden case (the POST response is the bare case, not an envelope).
export function toCreatedGoldenConversation(payload: unknown): GoldenConversation {
	const parsed = goldenConversationSchema.safeParse(payload);
	if (!parsed.success) {
		throw new Error(`Invalid golden conversation payload: ${parsed.error.message}`);
	}
	return toGoldenConversation(parsed.data);
}

// Create-request boundary schema enforcing the same length caps as the backend before the POST. The turns and the
// assertion are capped on their serialized JSON length (what the server stores), so the FE rejects the same over-long
// input the server would and the form can surface a precise field message instead of a generic 400.
const createGoldenConversationRequestSchema = z.object({
	title: z.string().max(GOLDEN_TITLE_MAX),
	inputTurns: z
		.array(goldenTurnSchema)
		.refine((turns) => JSON.stringify(turns).length <= GOLDEN_INPUT_TURNS_MAX, {
			path: ["inputTurns"],
		}),
	assertion: goldenAssertionSchema
		.refine((assertion) => JSON.stringify(assertion).length <= GOLDEN_ASSERTION_MAX, {
			path: ["assertion"],
		})
		.optional(),
	rubric: z.string().max(GOLDEN_RUBRIC_MAX).optional(),
	enabled: z.boolean().optional(),
});

// Which capped field (if any) a create request exceeds, so the add form can surface a precise message. Returns null
// when the request is within all caps. Mirrors the backend GoldenConversationService validation order.
export type GoldenFieldOverLimit = "title" | "inputTurns" | "assertion" | "rubric";

export function findGoldenFieldOverLimit(request: CreateGoldenConversationRequestDto): GoldenFieldOverLimit | null {
	const parsed = createGoldenConversationRequestSchema.safeParse(request);
	if (parsed.success) {
		return null;
	}
	const field = parsed.error.issues[0]?.path[0];
	return field === "title" || field === "inputTurns" || field === "assertion" || field === "rubric" ? field : null;
}

// Playbook P4/P5 — the 409 body returned when a blocked promote is attempted: a machine status + a human-readable
// reason. `EvalRequired` = no eval has run; `EvalRegressed` = the latest eval failed (a prior-good case broke);
// `EvalStale` = the action was edited since the last passing eval; `CapReached` (Playbook P5) = the agent is
// already at its MaxEnabledActions bound, so the operator must archive/disable an Enabled action before promoting.
export type PromoteConflictStatus = "EvalRequired" | "EvalRegressed" | "EvalStale" | "CapReached";

export interface PromoteConflictBody {
	readonly status: PromoteConflictStatus;
	readonly reason: string;
}

export const promoteConflictStatusSchema = z.enum(["EvalRequired", "EvalRegressed", "EvalStale", "CapReached"]);

const promoteConflictBodySchema = z.object({
	status: promoteConflictStatusSchema,
	reason: z.string(),
});

// Best-effort parse of the 409 promote-conflict body. Returns null when the payload does not match the contract
// (so the caller can fall back to a generic message rather than throwing).
export function parsePromoteConflictBody(payload: unknown): PromoteConflictBody | null {
	const parsed = promoteConflictBodySchema.safeParse(payload);
	return parsed.success ? { status: parsed.data.status, reason: parsed.data.reason } : null;
}
