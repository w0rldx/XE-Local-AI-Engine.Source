import { describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import { RESPONSE_VALIDATION_PROBLEM_TITLE } from "@/core/api/ResponseValidation";
import {
	applyBenchmarkEvent,
	type BenchmarkOutputPart,
	benchmarkRankExclusionReasons,
	benchmarkRunEventSchema,
	isBenchmarkRunTruncated,
	isUnsupportedKvCacheTypeError,
	toBenchmarkRankExclusionReason,
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

const problem = (title: string, detail: string, code?: string): ProblemDetails =>
	({ type: "about:blank", title, status: 422, detail, ...(code === undefined ? {} : { code }) }) as ProblemDetails;

describe("isUnsupportedKvCacheTypeError", () => {
	// The node answers 422 for the KV refusal AND for an ineligible model/agent, and the local hey-api response
	// validator reports a contract mismatch under 422 as well. Only the first is fixed by picking f16, so the code
	// extension — not the status — decides.
	it("accepts only the KV refusal among the 422s", () => {
		const refusal = new ApiError(422, problem("Unprocessable Entity", "q4_0 is not supported.", "UnsupportedKvCacheType"));
		const ineligible = new ApiError(422, problem("Unprocessable Entity", "The model is not eligible.", "IneligibleModel"));
		const validation = new ApiError(
			422,
			problem(RESPONSE_VALIDATION_PROBLEM_TITLE, "The server returned a response in an unexpected shape."),
		);

		expect(isUnsupportedKvCacheTypeError(refusal)).toBe(true);
		expect(isUnsupportedKvCacheTypeError(ineligible)).toBe(false);
		expect(isUnsupportedKvCacheTypeError(validation)).toBe(false);
	});

	it("rejects any other failure", () => {
		const conflict = problem("Conflict", "The run changed.", "VersionConflict");
		expect(isUnsupportedKvCacheTypeError(new ApiError(409, conflict))).toBe(false);
		expect(isUnsupportedKvCacheTypeError(new Error("offline"))).toBe(false);
		expect(isUnsupportedKvCacheTypeError(null)).toBe(false);
	});
});

describe("isBenchmarkRunTruncated", () => {
	// The status alone cannot answer this: a truncated run is Succeeded. Only the stop reason separates a finished
	// answer from a fragment, and the badge, the judge notice and the rank exclusion all key off this one predicate.
	it("recognises only the length stop reason, case-insensitively", () => {
		expect(isBenchmarkRunTruncated({ primaryStopReason: "length" })).toBe(true);
		expect(isBenchmarkRunTruncated({ primaryStopReason: "Length" })).toBe(true);
		expect(isBenchmarkRunTruncated({ primaryStopReason: "stop" })).toBe(false);
		expect(isBenchmarkRunTruncated({ primaryStopReason: "tool_calls" })).toBe(false);
		// A run frozen before the column existed was never measured; it must not read as truncated OR as complete.
		expect(isBenchmarkRunTruncated({ primaryStopReason: null })).toBe(false);
	});

	it("keeps truncated in the exhaustive rank-exclusion vocabulary", () => {
		expect(benchmarkRankExclusionReasons).toContain("truncated");
		expect(toBenchmarkRankExclusionReason("truncated")).toBe("truncated");
	});
});
