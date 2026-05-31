import { z } from "zod";

// Playbook P2 — read-only feedback insights for one agent. This is a pure analytics view over the feedback
// already persisted node-locally (message_feedback ⋈ conversations ⋈ tool_events). NO mutations, no AI
// generation, no PlaybookAction writes — it only aggregates existing up/down ratings + verbatim comment
// exemplars so an operator can read where an agent is doing well or poorly.
//
// The wire `rating` is the string "up"/"down" (enum-free on the wire). Counts are non-negative integers;
// `downRate` is the fraction of down-rated feedback (0..1); `meetsThreshold` encodes the "recurring, not
// n=1" gate (total >= minOccurrenceThreshold) the backend computes. The boundary is validated with Zod
// `safeParse` so a malformed payload surfaces as a thrown error rather than a silently-wrong panel.

export type FeedbackRating = "up" | "down";

// Domain view-models. Timestamps are epoch milliseconds (long on the wire).
export interface OverallFeedback {
	readonly total: number;
	readonly up: number;
	readonly down: number;
	readonly downRate: number;
	readonly meetsThreshold: boolean;
}

export interface ToolFeedbackBreakdown {
	readonly toolName: string;
	readonly total: number;
	readonly up: number;
	readonly down: number;
	readonly downRate: number;
	readonly meetsThreshold: boolean;
}

export interface FeedbackExemplar {
	readonly rating: FeedbackRating;
	readonly comment: string;
	readonly messageId: string;
	readonly conversationId: string;
	readonly createdAtUtc: number;
	readonly truncated: boolean;
}

export interface FeedbackInsights {
	readonly agentDefinitionId: string;
	readonly agentName: string;
	readonly generatedAtUtc: number;
	readonly minOccurrenceThreshold: number;
	readonly overall: OverallFeedback;
	readonly byTool: readonly ToolFeedbackBreakdown[];
	readonly exemplars: readonly FeedbackExemplar[];
}

const ratingSchema = z.enum(["up", "down"]);

const overallSchema = z.object({
	total: z.number(),
	up: z.number(),
	down: z.number(),
	downRate: z.number(),
	meetsThreshold: z.boolean(),
});

const toolSchema = z.object({
	toolName: z.string(),
	total: z.number(),
	up: z.number(),
	down: z.number(),
	downRate: z.number(),
	meetsThreshold: z.boolean(),
});

const exemplarSchema = z.object({
	rating: ratingSchema,
	comment: z.string(),
	messageId: z.string(),
	conversationId: z.string(),
	createdAtUtc: z.number(),
	truncated: z.boolean(),
});

// Boundary schema for the GET /agents/{id}/feedback-insights response.
export const feedbackInsightsSchema = z.object({
	agentDefinitionId: z.string(),
	agentName: z.string(),
	generatedAtUtc: z.number(),
	minOccurrenceThreshold: z.number(),
	overall: overallSchema,
	byTool: z.array(toolSchema),
	exemplars: z.array(exemplarSchema),
});

// The validated wire shape (camelCase, matches the agents surface). Kept separate from the readonly domain
// view-model so the boundary owns parsing and the rest of the feature consumes the immutable shape.
export type FeedbackInsightsDto = z.infer<typeof feedbackInsightsSchema>;

// Validate + deserialize the wire payload at the boundary with safeParse. A malformed payload throws a
// descriptive error (caught by the TanStack Query error path) rather than rendering partial/wrong analytics.
export function toFeedbackInsights(payload: unknown): FeedbackInsights {
	const parsed = feedbackInsightsSchema.safeParse(payload);
	if (!parsed.success) {
		throw new Error(`Invalid feedback insights payload: ${parsed.error.message}`);
	}

	const dto = parsed.data;
	return {
		agentDefinitionId: dto.agentDefinitionId,
		agentName: dto.agentName,
		generatedAtUtc: dto.generatedAtUtc,
		minOccurrenceThreshold: dto.minOccurrenceThreshold,
		overall: { ...dto.overall },
		byTool: dto.byTool.map((tool) => ({ ...tool })),
		exemplars: dto.exemplars.map((exemplar) => ({ ...exemplar })),
	};
}
