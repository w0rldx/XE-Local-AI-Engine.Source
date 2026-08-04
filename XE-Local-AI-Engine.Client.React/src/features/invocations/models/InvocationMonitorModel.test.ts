import type { TFunction } from "i18next";
import { describe, expect, it } from "vitest";

import {
	type InvocationSummaryInput,
	buildInvocationSummary,
	formatInvocationDuration,
	getInvocationStatusColor,
	isInvocationActive,
	sortInvocationHistory,
} from "@/features/invocations/models/InvocationMonitorModel";

// A minimal stand-in for i18next's `t`: returns the English default with {{placeholders}} interpolated, so the tests
// exercise the real sentence composition (verb + duration + model + counts + tool note) without an i18n runtime.
const t = ((_key: string, defaultValue: string, options?: Record<string, unknown>): string => {
	if (!options) {
		return defaultValue;
	}
	return defaultValue.replace(/\{\{(\w+)\}\}/g, (_match, name: string) => String(options[name] ?? ""));
}) as unknown as TFunction;

function summaryInput(overrides: Partial<InvocationSummaryInput> = {}): InvocationSummaryInput {
	return {
		status: "Completed",
		modelUsed: "gemma3",
		durationMs: 4200,
		streamedChunkCount: 128,
		streamedThinkingChunkCount: 40,
		pendingToolCallCount: 0,
		hasPendingApproval: false,
		hasPendingQuestion: false,
		error: null,
		failureCategory: null,
		...overrides,
	};
}

describe("invocation monitor model", () => {
	it("formats durations", () => {
		expect(formatInvocationDuration(250)).toBe("250 ms");
		expect(formatInvocationDuration(1200)).toBe("1.2 s");
		expect(formatInvocationDuration(120_000)).toBe("2.0 min");
	});

	it("maps status colors and active state", () => {
		expect(getInvocationStatusColor("Running")).toBe("blue");
		expect(getInvocationStatusColor("Completed")).toBe("green");
		expect(getInvocationStatusColor("Failed")).toBe("red");
		expect(isInvocationActive("Assigned")).toBe(true);
		expect(isInvocationActive("Completed")).toBe(false);
	});

	it("sorts history by newest completion first", () => {
		const sorted = sortInvocationHistory([
			createHistory("old", "2026-05-25T10:00:00Z"),
			createHistory("new", "2026-05-25T10:05:00Z"),
		]);

		expect(sorted.map((entry) => entry.invocationId)).toEqual(["new", "old"]);
	});
});

describe("buildInvocationSummary", () => {
	it("summarizes a completed run with duration, model, and chunk counts", () => {
		expect(buildInvocationSummary(summaryInput(), t)).toBe(
			"Completed in 4.2 s with gemma3 — 128 output chunks, 40 reasoning, no tool calls.",
		);
	});

	it("summarizes a failed run with the error reason", () => {
		expect(buildInvocationSummary(summaryInput({ status: "Failed", error: "Model load timeout" }), t)).toBe(
			"Failed after 4.2 s with gemma3 — Model load timeout.",
		);
	});

	it("falls back to the failure category when there is no error message", () => {
		expect(buildInvocationSummary(summaryInput({ status: "Failed", error: null, failureCategory: "Timeout" }), t)).toBe(
			"Failed after 4.2 s with gemma3 — Timeout.",
		);
	});

	it("summarizes an active running run with 'so far' counts and pending tool calls", () => {
		expect(buildInvocationSummary(summaryInput({ status: "Running", durationMs: undefined, pendingToolCallCount: 2 }), t)).toBe(
			"Running on gemma3 — 128 output chunks, 40 reasoning so far (2 pending tool call(s)).",
		);
	});

	it("notes a pending approval on an active run", () => {
		expect(buildInvocationSummary(summaryInput({ status: "Running", hasPendingApproval: true }), t)).toContain(
			"awaiting tool approval",
		);
	});

	// A turn parked on an ask_user question otherwise reads as an ordinary running run with nothing pending.
	it("notes a pending question on an active run", () => {
		expect(buildInvocationSummary(summaryInput({ status: "Running", hasPendingQuestion: true }), t)).toContain(
			"awaiting an answer to a question",
		);
	});

	it("summarizes pending/assigned as waiting to start", () => {
		expect(buildInvocationSummary(summaryInput({ status: "Pending" }), t)).toBe("Waiting to start on gemma3.");
		expect(buildInvocationSummary(summaryInput({ status: "Assigned" }), t)).toBe("Waiting to start on gemma3.");
	});

	it("summarizes a cancelled run", () => {
		expect(buildInvocationSummary(summaryInput({ status: "Cancelled" }), t)).toBe("Cancelled after 4.2 s with gemma3.");
	});
});

function createHistory(invocationId: string, completedAt: string) {
	return {
		invocationId,
		conversationId: "conversation",
		status: "Completed" as const,
		modelUsed: "qwen3:8b",
		startedAt: "2026-05-25T09:59:00Z",
		completedAt,
		durationMs: 60_000,
		error: null,
		failureCategory: null,
		streamedChunkCount: 1,
		streamedThinkingChunkCount: 0,
		traceId: null,
	};
}
