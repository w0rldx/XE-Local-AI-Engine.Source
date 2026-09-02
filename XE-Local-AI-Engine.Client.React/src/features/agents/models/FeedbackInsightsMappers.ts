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

// Maps optional generated wire fields into required domain values; validation remains at the API boundary.
// Exemplar redaction and truncation remain backend-owned.

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
