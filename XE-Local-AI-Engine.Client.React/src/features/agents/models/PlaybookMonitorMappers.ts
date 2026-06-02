import type {
	XeLocalAiEngineClientEndpointsAgentsV1AgentPlaybookMonitorResponse,
	XeLocalAiEngineClientEndpointsAgentsV1PlaybookActionMonitorItemResponse,
	XeLocalAiEngineClientEndpointsAgentsV1PlaybookRetrievalResponse,
} from "@/core/api/generated";
import type {
	PlaybookMonitor,
	PlaybookMonitorItem,
	PlaybookMonitorStatus,
	PlaybookRetrievalConfig,
	PlaybookRetrievalRanker,
} from "@/features/agents/models/PlaybookMonitorModels";

// Maps the generated (OpenAPI) playbook-monitor response to the stricter domain view-model the panel depends on.
// Generated fields are all optional (`x?: T`), so each mapper coalesces to a required value with a safe default.
// Boundary validation + ApiError convergence are owned by the generated zod validator + withResponseValidation
// bridge (this replaces the feature's former hand-zod safeParse). The monitoring signal is coarse + agent-level
// (flag-only, never auto-disable); the mapper surfaces only what the response carries.

const DEFAULT_MONITOR_STATUS: PlaybookMonitorStatus = "InsufficientData";
// Lexical is the effective default + auto-fallback (active whenever no embedding model is configured).
const DEFAULT_RETRIEVAL_RANKER: PlaybookRetrievalRanker = "lexical";

function toMonitorItem(dto: XeLocalAiEngineClientEndpointsAgentsV1PlaybookActionMonitorItemResponse): PlaybookMonitorItem {
	return {
		actionId: dto.actionId ?? "",
		enabledAtUtc: dto.enabledAtUtc ?? 0,
		beforeDownRate: dto.beforeDownRate ?? 0,
		afterDownRate: dto.afterDownRate ?? 0,
		afterSampleSize: dto.afterSampleSize ?? 0,
		// The generated status union carries the same values as the domain union; default a missing value.
		status: dto.status ?? DEFAULT_MONITOR_STATUS,
		flagged: dto.flagged ?? false,
		facetToolName: dto.facetToolName ?? null,
	};
}

function toRetrievalConfig(
	dto: XeLocalAiEngineClientEndpointsAgentsV1PlaybookRetrievalResponse | undefined,
): PlaybookRetrievalConfig {
	return {
		threshold: dto?.threshold ?? 0,
		topK: dto?.topK ?? 0,
		// ranker is "embedding"/"lexical" on the wire (generated widens to string); anything but "embedding" is
		// treated as the lexical default so the domain union stays total.
		ranker: dto?.ranker === "embedding" ? "embedding" : DEFAULT_RETRIEVAL_RANKER,
		embeddingModel: dto?.embeddingModel ?? null,
	};
}

export function toPlaybookMonitor(dto: XeLocalAiEngineClientEndpointsAgentsV1AgentPlaybookMonitorResponse): PlaybookMonitor {
	return {
		items: (dto.items ?? []).map(toMonitorItem),
		retrieval: toRetrievalConfig(dto.retrieval),
	};
}
