import type { BenchmarkOutputPart } from "@/features/benchmarks/models/BenchmarkRunModels";
import type { ChatMessagePart, ToolCallState } from "@/features/chat/models/ChatModels";

export * from "@/features/benchmarks/models/BenchmarkFidelityModels";
export * from "@/features/benchmarks/models/BenchmarkLiveStreamModels";
export * from "@/features/benchmarks/models/BenchmarkProjectModels";
export * from "@/features/benchmarks/models/BenchmarkRankingModels";
export * from "@/features/benchmarks/models/BenchmarkRunModels";

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
			const id = part.toolCallId || `benchmark-tool-${sequence}`;
			tools.set(id, rendered.length);
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
