import type { BenchmarkOutputPart, BenchmarkRepeatMode } from "@/features/benchmarks/models/BenchmarkRunModels";
import type { ChatMessagePart, ToolCallState } from "@/features/chat/models/ChatModels";

export * from "@/features/benchmarks/models/BenchmarkFidelityModels";
export * from "@/features/benchmarks/models/BenchmarkLiveStreamModels";
export * from "@/features/benchmarks/models/BenchmarkProjectModels";
export * from "@/features/benchmarks/models/BenchmarkRankingModels";
export * from "@/features/benchmarks/models/BenchmarkReviewModels";
export * from "@/features/benchmarks/models/BenchmarkRunModels";

/** One launch-matrix submission: every model x KV-cache combination the operator picked, plus the shared run knobs. */
export interface BenchmarkMatrixSelection {
	items: { modelName: string; kvCacheType?: string }[];
	repeatCount: number;
	warmup: boolean;
	repeatMode: BenchmarkRepeatMode;
	/** Null = the node's own default; omitted entirely in throughput mode, which is deterministic by definition. */
	answerVarianceTemperature: number | null;
}

function toolState(isError: boolean | null | undefined): ToolCallState {
	return isError ? "failed" : "received";
}

export function toChatMessageParts(parts: readonly BenchmarkOutputPart[]): ChatMessagePart[] {
	const rendered: ChatMessagePart[] = [];
	const tools = new Map<string, number>();
	let sequence = 0;
	for (const part of parts) {
		sequence += 1;
		if ((part.kind === "output" || part.kind === "text") && part.content) {
			rendered.push({ kind: "text", id: `benchmark-text-${sequence}`, sequence, text: part.content });
			continue;
		}
		if (part.kind === "reasoning" && part.content) {
			rendered.push({ kind: "reasoning", id: `benchmark-reasoning-${sequence}`, sequence, text: part.content });
			continue;
		}
		if (part.kind === "tool_call") {
			const callId = part.toolCallId || `benchmark-tool-${sequence}`;
			// A model-supplied id can repeat within one answer. The result pairs with the LATEST call of that id, but the
			// rendered part needs an id of its own, or the transcript collapses two calls into one React key.
			const id = tools.has(callId) ? `${callId}-${sequence}` : callId;
			tools.set(callId, rendered.length);
			rendered.push({
				kind: "tool",
				id,
				sequence,
				name: part.toolName || "tool",
				state: "requesting",
				args: part.arguments ?? undefined,
			});
			continue;
		}
		if (part.kind === "tool_result") {
			const id = part.toolCallId || `benchmark-tool-${sequence}`;
			const index = tools.get(id);
			if (index !== undefined) {
				const existing = rendered[index];
				if (existing?.kind === "tool") {
					rendered[index] = {
						...existing,
						state: toolState(part.isError),
						result: part.result ?? undefined,
					};
				}
			} else {
				rendered.push({
					kind: "tool",
					id,
					sequence,
					name: part.toolName || "tool",
					state: toolState(part.isError),
					result: part.result ?? undefined,
				});
			}
		}
	}
	return rendered.sort((left, right) => left.sequence - right.sequence);
}

/** One tool call of a run with its measured execution time, for the run pane's timing list. */
export interface BenchmarkToolCall {
	/** Unique within the answer: position plus call id, since the model-supplied id can repeat. */
	key: string;
	id: string;
	name: string;
	/** Execution time from the `tool_result`, or null when the node did not time the call. */
	durationMs: number | null;
	/** The same tool with byte-identical arguments was already called earlier in this answer. */
	repeated: boolean;
}

/**
 * The run's tool calls in call order, each joined to its result's duration. A repeat is detected here rather than
 * stored: the node keeps no attempt counter, and an identical (name, arguments) pair is visible from the parts alone.
 */
export function benchmarkToolCalls(parts: readonly BenchmarkOutputPart[]): BenchmarkToolCall[] {
	const durations = new Map<string, number>();
	for (const part of parts) {
		if (part.kind === "tool_result" && part.toolCallId && typeof part.durationMs === "number") {
			durations.set(part.toolCallId, part.durationMs);
		}
	}
	const seen = new Set<string>();
	return parts
		.filter((part) => part.kind === "tool_call")
		.map((part, index) => {
			const signature = JSON.stringify([part.toolName ?? "", part.arguments ?? ""]);
			const repeated = seen.has(signature);
			seen.add(signature);
			const id = part.toolCallId || `benchmark-tool-${index}`;
			return { key: `${index}:${id}`, id, name: part.toolName || "tool", durationMs: durations.get(id) ?? null, repeated };
		});
}

/** A tool execution time as the run pane prints it: one decimal of seconds. */
export const formatBenchmarkToolSeconds = (milliseconds: number): string => `${(milliseconds / 1000).toFixed(1)} s`;
