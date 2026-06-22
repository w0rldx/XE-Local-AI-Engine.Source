import type { XeLocalAiEngineClientEndpointsAgentsV1AgentExecutionLogResponse } from "@/core/api/generated";

// Per-run diagnostics for an agent (adaptive-memory observability). METADATA ONLY — the backend records no message
// or exception text here (errorClass is the exception TYPE name, never its message), so the table is safe to render
// verbatim. Timestamps are epoch milliseconds (long on the wire). Token counts and ids degrade to null when the
// streaming path did not populate them.
export interface AgentExecutionLog {
	readonly id: string;
	readonly agentDefinitionId: string;
	readonly conversationId: string | null;
	readonly messageId: string | null;
	readonly modelName: string;
	readonly configHash: string;
	readonly latencyMs: number;
	readonly promptTokens: number | null;
	readonly completionTokens: number | null;
	readonly success: boolean;
	// The exception TYPE name when the run failed (e.g. "TimeoutException"); null on success. Never the message.
	readonly errorClass: string | null;
	readonly createdAtUtc: number;
}

// Project a generated execution-log response into the immutable domain view-model. Every field is optional on the
// wire, so each coalesces to a safe default (null for the optional metadata, 0 for counters).
export function toAgentExecutionLog(
	dto: XeLocalAiEngineClientEndpointsAgentsV1AgentExecutionLogResponse,
): AgentExecutionLog {
	return {
		id: dto.id ?? "",
		agentDefinitionId: dto.agentDefinitionId ?? "",
		conversationId: dto.conversationId ?? null,
		messageId: dto.messageId ?? null,
		modelName: dto.modelName ?? "",
		configHash: dto.configHash ?? "",
		latencyMs: dto.latencyMs ?? 0,
		promptTokens: dto.promptTokens ?? null,
		completionTokens: dto.completionTokens ?? null,
		success: dto.success ?? false,
		errorClass: dto.errorClass ?? null,
		createdAtUtc: dto.createdAtUtc ?? 0,
	};
}
