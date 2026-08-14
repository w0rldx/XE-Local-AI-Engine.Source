import { describe, expect, it } from "vitest";

import {
	applyBenchmarkEvent,
	type BenchmarkOutputPart,
	benchmarkRunEventSchema,
	toChatMessageParts,
} from "@/features/benchmarks/models/BenchmarkModels";

describe("benchmark output mapping", () => {
	it("accumulates output and reasoning deltas without duplicating adjacent parts", () => {
		const first = benchmarkRunEventSchema.parse({ runId: "r", sequence: 1, kind: "OutputDelta", payload: { content: "hel" } });
		const second = benchmarkRunEventSchema.parse({ runId: "r", sequence: 2, kind: "OutputDelta", payload: { content: "lo" } });
		const reasoning = benchmarkRunEventSchema.parse({
			runId: "r",
			sequence: 3,
			kind: "ReasoningDelta",
			payload: { content: "why" },
		});
		const parts = applyBenchmarkEvent(applyBenchmarkEvent(applyBenchmarkEvent([], first), second), reasoning);
		expect(parts).toEqual([
			{ kind: "output", content: "hello" },
			{ kind: "reasoning", content: "why" },
		]);
	});

	it("combines tool request and result into the existing MessageParts contract", () => {
		const parts: BenchmarkOutputPart[] = [
			{ kind: "tool_call", toolCallId: "call-1", toolName: "clock", arguments: "{}" },
			{ kind: "tool_result", toolCallId: "call-1", result: "12:00", isError: false },
		];
		expect(toChatMessageParts(parts)).toEqual([
			expect.objectContaining({ kind: "tool", id: "call-1", name: "clock", state: "received", result: "12:00" }),
		]);
	});
});
