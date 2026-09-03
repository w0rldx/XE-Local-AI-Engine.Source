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

// Maps optional generated wire fields into required domain values; validation remains at the API boundary.
// Generated and domain enum values stay aligned.
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
		hasPendingQuestion: dto.hasPendingQuestion ?? false,
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
