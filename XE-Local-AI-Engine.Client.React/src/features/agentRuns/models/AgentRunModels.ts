import type { XeLocalAiEngineClientEndpointsAgentHomeV1AgentHomeRunDto as AgentHomeRunDto } from "@/core/api/generated";

/** Rows per page the run history starts on, and the sizes its footer offers. */
export const agentRunPageSize = 25;
export const agentRunPageSizeOptions: readonly number[] = [10, 25, 50, 100];

/** The node's closed apply vocabulary. Anything else is treated as "none", never rendered raw. */
export type AgentRunApplyState = "none" | "applied" | "rejected";

/** One run as the page renders it: the wire shape with every optional field resolved. */
export interface AgentRunView {
	readonly runId: string;
	readonly startedAtUtc: string;
	readonly outcome: string;
	readonly patchExported: boolean;
	readonly changedFileCount: number | null;
	readonly applyState: AgentRunApplyState;
	readonly conversationId: string | null;
	readonly sizeBytes: number;
}

const applyStates: readonly AgentRunApplyState[] = ["none", "applied", "rejected"];

/**
 * The outcome tokens the node emits. The page looks up a label per token and falls back to the "unknown" label for
 * anything else, so a token this build has never heard of reads as unknown rather than as raw server text.
 */
const agentRunOutcomes: readonly string[] = [
	"unknown",
	"cancelled",
	"NotRun",
	"Completed",
	"ToolCallBudgetExceeded",
	"TimeBudgetExceeded",
	"Failed",
];

export function toAgentRunView(dto: AgentHomeRunDto): AgentRunView {
	return {
		runId: dto.runId,
		startedAtUtc: dto.startedAtUtc,
		outcome: agentRunOutcomes.includes(dto.outcome) ? dto.outcome : "unknown",
		patchExported: dto.patchExported,
		changedFileCount: dto.changedFileCount ?? null,
		applyState: applyStates.find((state) => state === dto.applyState) ?? "none",
		conversationId: dto.conversationId ?? null,
		sizeBytes: dto.sizeBytes,
	};
}

/** Mantine badge colour per outcome. Green only for a run that finished on its own terms. */
export function agentRunOutcomeColor(outcome: string): string {
	switch (outcome) {
		case "Completed":
			return "teal";
		case "Failed":
			return "red";
		case "cancelled":
			return "gray";
		case "ToolCallBudgetExceeded":
		case "TimeBudgetExceeded":
			return "yellow";
		default:
			return "gray";
	}
}

/** Bytes as KB/MB/GB, one decimal above a kilobyte. The node reports an exact count; the page shows a size. */
export function formatAgentRunSize(sizeBytes: number): string {
	const units = ["B", "KB", "MB", "GB"];
	let value = Math.max(sizeBytes, 0);
	let unit = 0;
	while (value >= 1024 && unit < units.length - 1) {
		value /= 1024;
		unit += 1;
	}
	return `${unit === 0 ? value : value.toFixed(1)} ${units[unit]}`;
}
