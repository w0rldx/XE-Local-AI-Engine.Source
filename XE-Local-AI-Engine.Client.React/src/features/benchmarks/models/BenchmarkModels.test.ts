import { describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import { RESPONSE_VALIDATION_PROBLEM_TITLE } from "@/core/api/ResponseValidation";
import type { BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import {
	applyBenchmarkEvent,
	type BenchmarkOutputPart,
	benchmarkBatchProgress,
	benchmarkRankExclusionReasons,
	benchmarkRunEventSchema,
	isBenchmarkRunIncomplete,
	isBenchmarkRunReasoningExhausted,
	isBenchmarkRunTruncated,
	isUnsupportedKvCacheTypeError,
	maxComparedBenchmarkRuns,
	toBenchmarkRankExclusionReason,
	toChatMessageParts,
	toggleBenchmarkRunSelection,
} from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkRunSummaryFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";

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

	// The node's `BenchmarkStopReasons.IsTruncated` counts reasoning-length as truncation and excludes it as
	// `truncated`. A UI that knew only `length` would show a rank-excluded run with no badge saying why.
	it("reads a reasoning-exhausted run as truncated, and says which budget ran out", () => {
		expect(isBenchmarkRunTruncated({ primaryStopReason: "reasoning-length" })).toBe(true);
		expect(isBenchmarkRunReasoningExhausted({ primaryStopReason: "reasoning-length" })).toBe(true);
		expect(isBenchmarkRunReasoningExhausted({ primaryStopReason: "length" })).toBe(false);
		expect(isBenchmarkRunIncomplete({ primaryStopReason: "reasoning-length" })).toBe(false);
	});

	// An answerless run is NOT truncated: no budget ran out, so raising one changes nothing.
	it("keeps incomplete apart from truncated, in both the predicate and the vocabulary", () => {
		expect(isBenchmarkRunIncomplete({ primaryStopReason: "incomplete" })).toBe(true);
		expect(isBenchmarkRunTruncated({ primaryStopReason: "incomplete" })).toBe(false);
		expect(benchmarkRankExclusionReasons).toContain("incomplete");
		expect(toBenchmarkRankExclusionReason("incomplete")).toBe("incomplete");
	});
});

describe("benchmark batch progress", () => {
	const run = (id: string, primaryStatus: BenchmarkRunSummary["primaryStatus"]) =>
		benchmarkRunSummaryFixture({ id, primaryStatus });

	it("counts a launch's runs by what they are doing, cancelled and failed alike", () => {
		const runs = [
			run("a", "Succeeded"),
			run("b", "Failed"),
			run("c", "Cancelled"),
			run("d", "Running"),
			run("e", "CancelRequested"),
			run("f", "Queued"),
			run("unrelated", "Succeeded"),
		];

		expect(benchmarkBatchProgress(runs, ["a", "b", "c", "d", "e", "f"])).toEqual({
			total: 6,
			done: 3,
			running: 2,
			queued: 1,
			failed: 2,
		});
	});

	// The table shows one page; a started run below the fold must not shrink the denominator it is counted against.
	it("counts a started run the loaded page does not carry as queued", () => {
		const progress = benchmarkBatchProgress([run("a", "Succeeded")], ["a", "not-loaded"]);

		expect(progress).toEqual({ total: 2, done: 1, running: 0, queued: 1, failed: 0 });
	});

	it("reads a finished batch as fully done", () => {
		const progress = benchmarkBatchProgress([run("a", "Succeeded"), run("b", "Failed")], ["a", "b"]);

		expect(progress.done).toBe(progress.total);
	});
});

// The compare table is one column per run and the live pane under it is a full transcript each, so the selection is
// capped. What the cap must not do is refuse a click: an operator working down a quant ladder means "and this one
// too", and a checkbox that silently does nothing reads as a broken table.
describe("toggleBenchmarkRunSelection", () => {
	it("prepends a new run, newest first", () => {
		expect(toggleBenchmarkRunSelection(["a"], "b")).toEqual(["b", "a"]);
	});

	it("removes a run that was already selected", () => {
		expect(toggleBenchmarkRunSelection(["b", "a"], "b")).toEqual(["a"]);
	});

	it("drops the oldest selection rather than refusing the click once the cap is reached", () => {
		const full = ["f", "e", "d", "c", "b", "a"];

		expect(toggleBenchmarkRunSelection(full, "g")).toEqual(["g", "f", "e", "d", "c", "b"]);
	});

	it("caps at six by default", () => {
		expect(maxComparedBenchmarkRuns).toBe(6);
		expect(toggleBenchmarkRunSelection(["f", "e", "d", "c", "b", "a"], "g")).toHaveLength(6);
	});

	it("deselecting never drops anything else, even at the cap", () => {
		expect(toggleBenchmarkRunSelection(["f", "e", "d", "c", "b", "a"], "a")).toEqual(["f", "e", "d", "c", "b"]);
	});
});
