// Read-only feedback insights for one agent. This is a pure analytics view over the feedback
// already persisted node-locally (message_feedback ⋈ conversations ⋈ tool_events). NO mutations, no AI
// generation, no PlaybookAction writes — it only aggregates existing up/down ratings + verbatim comment
// exemplars so an operator can read where an agent is doing well or poorly.
//
// The wire `rating` is the string "up"/"down" (enum-free on the wire). Counts are non-negative integers;
// `downRate` is the fraction of down-rated feedback (0..1); `meetsThreshold` encodes the "recurring, not
// n=1" gate (total >= minOccurrenceThreshold) the backend computes. Boundary validation is owned by the
// generated hey-api zod validator + the withResponseValidation bridge; FeedbackInsightsMappers projects the
// validated wire shape into these immutable domain view-models.

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
