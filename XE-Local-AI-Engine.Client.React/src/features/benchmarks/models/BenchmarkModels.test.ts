import { describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import { RESPONSE_VALIDATION_PROBLEM_TITLE } from "@/core/api/ResponseValidation";
import {
	applyBenchmarkEvent,
	type BenchmarkOutputPart,
	benchmarkRunEventSchema,
	isUnsupportedKvCacheTypeError,
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

const problem = (title: string, detail: string): ProblemDetails => ({ type: "about:blank", title, status: 422, detail });

describe("isUnsupportedKvCacheTypeError", () => {
	// 422 is ALSO the status the local hey-api response validator reports a contract mismatch under, and telling the
	// operator to "pick f16 explicitly" for a malformed response would send them chasing the wrong thing.
	it("accepts the node's refusal and rejects a local response-validation failure", () => {
		const refusal = new ApiError(422, problem("Unprocessable Entity", "q4_0 is not supported."));
		const validation = new ApiError(
			422,
			problem(RESPONSE_VALIDATION_PROBLEM_TITLE, "The server returned a response in an unexpected shape."),
		);

		expect(isUnsupportedKvCacheTypeError(refusal)).toBe(true);
		expect(isUnsupportedKvCacheTypeError(validation)).toBe(false);
	});

	it("rejects any other failure", () => {
		expect(isUnsupportedKvCacheTypeError(new ApiError(409, problem("Conflict", "The run changed.")))).toBe(false);
		expect(isUnsupportedKvCacheTypeError(new Error("offline"))).toBe(false);
		expect(isUnsupportedKvCacheTypeError(null)).toBe(false);
	});
});
