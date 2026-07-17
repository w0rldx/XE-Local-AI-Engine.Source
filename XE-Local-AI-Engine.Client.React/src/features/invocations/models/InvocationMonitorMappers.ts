import type {
	XeLocalAiEngineClientEndpointsInvocationsV1InvocationCurrentResponse,
	XeLocalAiEngineClientEndpointsInvocationsV1InvocationHistoryResponse,
	XeLocalAiEngineClientEndpointsInvocationsV1InvocationMonitorResponse,
} from "@/core/api/generated";
import type {
	InvocationCurrentDto,
	InvocationHistoryDto,
	InvocationMonitorDto,
	InvocationStatusDto,
} from "@/features/invocations/models/InvocationMonitorModel";

// Maps the generated (OpenAPI) invocation-monitor response to the stricter domain view-model the page depends
// on. The generated types are the single source of truth for the wire shape; their fields are all optional
// (`x?: T`), so each mapper coalesces every field to a required value with a sensible default. The generated
// InvocationStatus enum is a string union with the SAME values as the domain union, so a present status maps
// through unchanged and an omitted one falls back to "Pending" (the backend's first/initial status).
const DEFAULT_STATUS: InvocationStatusDto = "Pending";

function toInvocationCurrent(
	dto: XeLocalAiEngineClientEndpointsInvocationsV1InvocationCurrentResponse,
): InvocationCurrentDto {
	return {
		invocationId: dto.invocationId ?? "",
		conversationId: dto.conversationId ?? "",
		status: dto.status ?? DEFAULT_STATUS,
		modelUsed: dto.modelUsed ?? null,
		startedAt: dto.startedAt ?? "",
		lastUpdatedAt: dto.lastUpdatedAt ?? "",
		completedAt: dto.completedAt ?? null,
		error: dto.error ?? null,
		failureCategory: dto.failureCategory ?? null,
		streamedChunkCount: dto.streamedChunkCount ?? 0,
		streamedThinkingChunkCount: dto.streamedThinkingChunkCount ?? 0,
		pendingToolCallCount: dto.pendingToolCallCount ?? 0,
		hasPendingApproval: dto.hasPendingApproval ?? false,
		traceId: dto.traceId ?? null,
	};
}

function toInvocationHistory(
	dto: XeLocalAiEngineClientEndpointsInvocationsV1InvocationHistoryResponse,
): InvocationHistoryDto {
	return {
		invocationId: dto.invocationId ?? "",
		conversationId: dto.conversationId ?? "",
		status: dto.status ?? DEFAULT_STATUS,
		modelUsed: dto.modelUsed ?? null,
		startedAt: dto.startedAt ?? "",
		completedAt: dto.completedAt ?? "",
		durationMs: dto.durationMs ?? 0,
		error: dto.error ?? null,
		failureCategory: dto.failureCategory ?? null,
		streamedChunkCount: dto.streamedChunkCount ?? 0,
		streamedThinkingChunkCount: dto.streamedThinkingChunkCount ?? 0,
		traceId: dto.traceId ?? null,
	};
}

export function toInvocationMonitor(
	dto: XeLocalAiEngineClientEndpointsInvocationsV1InvocationMonitorResponse,
): InvocationMonitorDto {
	return {
		current: dto.current ? toInvocationCurrent(dto.current) : null,
		history: (dto.history ?? []).map(toInvocationHistory),
		historyCapacity: dto.historyCapacity ?? 0,
	};
}
