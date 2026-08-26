import { z } from "zod";

import type {
	BenchmarkJudgeState,
	BenchmarkOutputPart,
	BenchmarkRunSummary,
	BenchmarkRunThroughput,
} from "@/features/benchmarks/models/BenchmarkRunModels";
import { toBenchmarkJudgeState } from "@/features/benchmarks/models/BenchmarkRunModels";

const benchmarkRunEventKinds = [
	"OutputDelta",
	"ReasoningDelta",
	"ToolCall",
	"ToolResult",
	"PrimaryState",
	"JudgeState",
	"Metrics",
	"TerminalSnapshotAvailable",
] as const;

export const benchmarkRunEventSchema = z.object({
	runId: z.string(),
	sequence: z.number().int().nonnegative(),
	kind: z.enum(benchmarkRunEventKinds),
	payload: z.object({
		content: z.string().nullish(),
		state: z.string().nullish(),
		toolCallId: z.string().nullish(),
		toolName: z.string().nullish(),
		arguments: z.string().nullish(),
		result: z.string().nullish(),
		isError: z.boolean().nullish(),
		effectiveContextTokens: z.number().int().nullish(),
		durationMs: z.number().int().nullish(),
		totalTokens: z.number().int().nullish(),
		tokensPerSecond: z.number().nullish(),
		runVersion: z.number().int().nullish(),
		ttftMs: z.number().nullish(),
		promptTokens: z.number().int().nullish(),
		promptTokensPerSecond: z.number().nullish(),
		generationTokens: z.number().int().nullish(),
		generationTokensPerSecond: z.number().nullish(),
		cachedPromptTokens: z.number().int().nullish(),
		segmentCount: z.number().int().nullish(),
	}),
});
export type BenchmarkRunEvent = z.infer<typeof benchmarkRunEventSchema>;

export const benchmarkReplayResetSchema = z.object({
	runId: z.string(),
	latestSequence: z.number().int().nonnegative(),
	runVersion: z.number().int().nonnegative(),
});
export type BenchmarkReplayReset = z.infer<typeof benchmarkReplayResetSchema>;

/**
 * The live corrections the hub has streamed since the last authoritative read. Kept as an overlay rather than written
 * into the query cache: the durable HTTP snapshot stays the authority, and a `TerminalSnapshotAvailable` refetch drops
 * the overlay again.
 */
export interface BenchmarkRunLiveOverlay {
	judgeState: BenchmarkJudgeState | null;
	effectiveContextTokens: number | null;
	durationMs: number | null;
	totalTokens: number | null;
	tokensPerSecond: number | null;
	/** Null until a `Metrics` event has arrived; the durable snapshot's own breakdown stays authoritative until then. */
	throughput: BenchmarkRunThroughput | null;
}

export const noBenchmarkRunLiveOverlay: BenchmarkRunLiveOverlay = {
	judgeState: null,
	effectiveContextTokens: null,
	durationMs: null,
	totalTokens: null,
	tokensPerSecond: null,
	throughput: null,
};

/** Applies the streamed corrections on top of the durable snapshot. Absent members leave the snapshot untouched. */
export function applyBenchmarkLiveOverlay<T extends BenchmarkRunSummary>(run: T, overlay: BenchmarkRunLiveOverlay): T {
	return {
		...run,
		judge: overlay.judgeState === null ? run.judge : { ...run.judge, state: overlay.judgeState },
		effectiveContextTokens: overlay.effectiveContextTokens ?? run.effectiveContextTokens,
		durationMs: overlay.durationMs ?? run.durationMs,
		totalTokens: overlay.totalTokens ?? run.totalTokens,
		tokensPerSecond: overlay.tokensPerSecond ?? run.tokensPerSecond,
		throughput: overlay.throughput ?? run.throughput,
	};
}

/** The live corrections one hub event carries, or null when it carries none. */
export function benchmarkLiveOverlayPatch(event: BenchmarkRunEvent): Partial<BenchmarkRunLiveOverlay> | null {
	if (event.kind === "JudgeState") {
		return { judgeState: toBenchmarkJudgeState(event.payload.state) };
	}
	if (event.kind === "Metrics") {
		return {
			effectiveContextTokens: event.payload.effectiveContextTokens ?? null,
			durationMs: event.payload.durationMs ?? null,
			totalTokens: event.payload.totalTokens ?? null,
			tokensPerSecond: event.payload.tokensPerSecond ?? null,
			throughput: {
				ttftMs: event.payload.ttftMs ?? null,
				promptTokens: event.payload.promptTokens ?? null,
				promptTokensPerSecond: event.payload.promptTokensPerSecond ?? null,
				generationTokens: event.payload.generationTokens ?? null,
				generationTokensPerSecond: event.payload.generationTokensPerSecond ?? null,
				cachedPromptTokens: event.payload.cachedPromptTokens ?? null,
				segmentCount: event.payload.segmentCount ?? null,
			},
		};
	}
	return null;
}

function mergeTextPart(parts: BenchmarkOutputPart[], kind: "output" | "reasoning", content: string): BenchmarkOutputPart[] {
	if (!content) {
		return parts;
	}
	const next = [...parts];
	const last = next.at(-1);
	if (last?.kind === kind) {
		next[next.length - 1] = { ...last, content: `${last.content ?? ""}${content}` };
	} else {
		next.push({ kind, content });
	}
	return next;
}

export function applyBenchmarkEvent(parts: BenchmarkOutputPart[], event: BenchmarkRunEvent): BenchmarkOutputPart[] {
	if (event.kind === "OutputDelta") {
		return mergeTextPart(parts, "output", event.payload.content ?? "");
	}
	if (event.kind === "ReasoningDelta") {
		return mergeTextPart(parts, "reasoning", event.payload.content ?? "");
	}
	if (event.kind === "ToolCall") {
		return [
			...parts,
			{
				kind: "tool_call",
				toolCallId: event.payload.toolCallId,
				toolName: event.payload.toolName,
				arguments: event.payload.arguments,
			},
		];
	}
	if (event.kind === "ToolResult") {
		return [
			...parts,
			{
				kind: "tool_result",
				toolCallId: event.payload.toolCallId,
				toolName: event.payload.toolName,
				result: event.payload.result,
				isError: event.payload.isError,
			},
		];
	}
	return parts;
}
