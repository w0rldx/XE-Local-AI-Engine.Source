import type {
	XeLocalAiEngineClientEndpointsAgentsV1AgentFeedbackInsightsResponse,
	XeLocalAiEngineClientEndpointsAgentsV1FeedbackExemplarResponse,
	XeLocalAiEngineClientEndpointsAgentsV1OverallFeedbackResponse,
	XeLocalAiEngineClientEndpointsAgentsV1ToolFeedbackResponse,
} from "@/core/api/generated";
import type {
	FeedbackExemplar,
	FeedbackInsights,
	OverallFeedback,
	ToolFeedbackBreakdown,
} from "@/features/agents/models/FeedbackInsightsModels";

// Maps the generated (OpenAPI) feedback-insights response to the stricter domain view-model the panel depends on.
// The generated types are the single source of truth for the wire shape; their fields are all optional (`x?: T`),
// so each mapper coalesces every field to a required value with a safe default. Boundary validation and ApiError
// convergence are owned by the generated zod validator (`validator: true`) + the withResponseValidation bridge at
// the hook — this mapper only projects the already-validated wire shape into the immutable domain shape (it no
// longer re-validates, replacing the feature's former hand-zod safeParse). Redaction is the backend's: the mapper
// surfaces only what the response carries (verbatim exemplar comments are already truncation-flagged server-side).

function toOverall(dto: XeLocalAiEngineClientEndpointsAgentsV1OverallFeedbackResponse | undefined): OverallFeedback {
	return {
		total: dto?.total ?? 0,
		up: dto?.up ?? 0,
		down: dto?.down ?? 0,
		downRate: dto?.downRate ?? 0,
		meetsThreshold: dto?.meetsThreshold ?? false,
	};
}

function toToolBreakdown(dto: XeLocalAiEngineClientEndpointsAgentsV1ToolFeedbackResponse): ToolFeedbackBreakdown {
	return {
		toolName: dto.toolName ?? "",
		total: dto.total ?? 0,
		up: dto.up ?? 0,
		down: dto.down ?? 0,
		downRate: dto.downRate ?? 0,
		meetsThreshold: dto.meetsThreshold ?? false,
	};
}

function toExemplar(dto: XeLocalAiEngineClientEndpointsAgentsV1FeedbackExemplarResponse): FeedbackExemplar {
	return {
		// rating is the binary "up"/"down" on the wire (the generated type widens it to string); anything that is
		// not "up" is treated as "down" so the domain union stays total.
		rating: dto.rating === "up" ? "up" : "down",
		comment: dto.comment ?? "",
		messageId: dto.messageId ?? "",
		conversationId: dto.conversationId ?? "",
		createdAtUtc: dto.createdAtUtc ?? 0,
		truncated: dto.truncated ?? false,
	};
}

export function toFeedbackInsights(dto: XeLocalAiEngineClientEndpointsAgentsV1AgentFeedbackInsightsResponse): FeedbackInsights {
	return {
		agentDefinitionId: dto.agentDefinitionId ?? "",
		agentName: dto.agentName ?? "",
		generatedAtUtc: dto.generatedAtUtc ?? 0,
		minOccurrenceThreshold: dto.minOccurrenceThreshold ?? 0,
		overall: toOverall(dto.overall),
		byTool: (dto.byTool ?? []).map(toToolBreakdown),
		exemplars: (dto.exemplars ?? []).map(toExemplar),
	};
}
