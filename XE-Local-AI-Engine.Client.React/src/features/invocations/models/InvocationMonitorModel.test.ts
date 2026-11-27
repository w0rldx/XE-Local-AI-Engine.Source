import i18next, { type TFunction } from "i18next";
import { describe, expect, it } from "vitest";

import {
	type InvocationSummaryInput,
	buildInvocationSummary,
	formatInvocationDuration,
	getInvocationErrorText,
	getInvocationStatusBadge,
	getInvocationStatusColor,
	isInvocationActive,
	sortInvocationHistory,
} from "@/features/invocations/models/InvocationMonitorModel";
import de from "@/locales/de.json";

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

	it("formats durations with the UI language's decimal separator", async () => {
		await i18next.changeLanguage("de");
		try {
			expect(formatInvocationDuration(1500)).toBe("1,5 s");
			expect(formatInvocationDuration(90_000)).toBe("1,5 min");
			expect(formatInvocationDuration(60_000_000)).toBe("1000,0 min");
		} finally {
			await i18next.changeLanguage("en");
		}
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

	// A run that fails before any model is served has no model; the sentence drops the clause instead of "with —".
	it("leaves the model out when the run has none", () => {
		expect(buildInvocationSummary(summaryInput({ status: "Failed", modelUsed: null, error: "Model load timeout" }), t)).toBe(
			"Failed after 4.2 s — Model load timeout.",
		);
		expect(buildInvocationSummary(summaryInput({ status: "Cancelled", modelUsed: "  ", durationMs: null }), t)).toBe(
			"Cancelled.",
		);
		expect(buildInvocationSummary(summaryInput({ status: "Pending", modelUsed: null }), t)).toBe("Waiting to start.");
		expect(buildInvocationSummary(summaryInput({ status: "Running", modelUsed: null }), t)).toBe(
			"Running — 128 output chunks, 40 reasoning so far (no tool calls).",
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

	// The current run has no durationMs on the wire, and the operator message already ends its own sentence.
	it("times a failed current run from its timestamps and ends the sentence once", () => {
		const run = summaryInput({
			status: "Failed",
			durationMs: undefined,
			startedAt: "2026-10-06T10:00:00Z",
			completedAt: "2026-10-06T10:00:12.500Z",
			error: "Invocation ended with a failure. See local logs for details.",
		});

		expect(buildInvocationSummary(run, t)).toBe(
			"Failed after 12.5 s with gemma3 — Invocation ended with a failure. See local logs for details.",
		);
	});

	// A step stopped at its provider-call cap is a designed bound, not a failure, on the badge and in the sentence.
	it("shows a run stopped at its call budget as that outcome, not as a failure", () => {
		const run = summaryInput({
			status: "Failed",
			failureCategory: "CallBudgetReached",
			error: "Stopped at the provider-call budget; a work session continues on its next step.",
		});

		expect(getInvocationStatusBadge(run, t)).toEqual({ label: "Stopped at call budget", color: "yellow" });
		expect(buildInvocationSummary(run, t)).toBe("Stopped at the provider-call budget after 4.2 s with gemma3.");
		expect(getInvocationStatusBadge(summaryInput({ status: "Failed", failureCategory: "Timeout" }), t).color).toBe("red");
	});

	it("translates a call-budget stop's result text from its category, not the English server sentence", () => {
		const keyOnly = ((key: string) => key) as unknown as TFunction;
		const run = summaryInput({ status: "Failed", failureCategory: "CallBudgetReached", error: "English server sentence." });

		expect(getInvocationErrorText(run, keyOnly)).toBe("pages.invocations.monitor.callBudgetReached");
		expect(de.pages.invocations.monitor.callBudgetReached).toContain("Arbeitssitzung");
		expect(getInvocationErrorText(summaryInput({ status: "Failed", error: "Other failure." }), keyOnly)).toBe("Other failure.");
	});

	// Without a usable error text the row reads the localized category label, never the raw wire value (ModelEjected).
	it("labels a failure without error text by its localized category", () => {
		const keyOnly = ((key: string) => key) as unknown as TFunction;
		const ejected = { status: "Failed" as const, failureCategory: "ModelEjected" };
		const label = "pages.chat.error.categoryLabels.ModelEjected";

		expect(getInvocationErrorText(summaryInput({ ...ejected, error: null }), keyOnly)).toBe(label);
		expect(getInvocationErrorText(summaryInput({ ...ejected, error: " \t " }), keyOnly)).toBe(label);
		expect(getInvocationErrorText(summaryInput({ status: "Failed", error: "  ", failureCategory: null }), keyOnly)).toBeNull();
		expect(de.pages.chat.error.categoryLabels.ModelEjected).toBe("Modell entladen");
	});

	// Only a category whose server text is a generic sentence trades it for its label; any other keeps the server text,
	// which can carry specifics such as a rate limit's retry interval.
	it("prefers the localized label over a generic server sentence, and keeps a specific one, in German", () => {
		i18next.addResourceBundle("de", "translation", de, true, true);
		const german = i18next.getFixedT("de");
		const ejected = summaryInput({ status: "Failed", failureCategory: "ModelEjected", error: "Invocation was cancelled." });
		const cancelled = summaryInput({ status: "Cancelled", failureCategory: "ModelEjected", error: "Invocation was cancelled." });

		expect(getInvocationErrorText(ejected, german)).toBe("Modell entladen");
		expect(getInvocationErrorText(cancelled, german)).toBe("Modell entladen");
		expect(buildInvocationSummary(ejected, german)).toContain("Modell entladen");
		expect(buildInvocationSummary(ejected, german)).not.toContain("Invocation was cancelled");
		expect(
			getInvocationErrorText(summaryInput({ status: "Failed", failureCategory: "BrandNew", error: "Server text." }), german),
		).toBe("Server text.");
		expect(getInvocationErrorText(summaryInput({ status: "Failed", error: "Server text." }), german)).toBe("Server text.");

		const generic = "Invocation ended with a failure. See local logs for details.";
		const rateLimitedGeneric = summaryInput({ status: "Failed", failureCategory: "ProviderRateLimited", error: generic });
		expect(getInvocationErrorText(rateLimitedGeneric, german)).toBe("Ratenlimit des Anbieters");
		expect(buildInvocationSummary(rateLimitedGeneric, german)).toContain("Ratenlimit des Anbieters");
		// A generic sentence for a category without a label stays: there is nothing more specific to show.
		expect(getInvocationErrorText(summaryInput({ status: "Failed", failureCategory: "BrandNew", error: generic }), german)).toBe(
			generic,
		);

		const retryAfter = "The provider's rate limit was reached. Retry after 30 seconds.";
		const rateLimited = summaryInput({ status: "Failed", failureCategory: "ProviderRateLimited", error: retryAfter });
		expect(getInvocationErrorText(rateLimited, german)).toBe(retryAfter);
		expect(buildInvocationSummary(rateLimited, german)).toContain("Retry after 30 seconds");
		const timedOut = summaryInput({ status: "Failed", failureCategory: "Timeout", error: "The response timed out." });
		expect(getInvocationErrorText(timedOut, german)).toBe("The response timed out.");
	});

	it("renders the localized category label inside the failed summary sentence", () => {
		const labels = ((key: string, defaultValue: string, options?: Record<string, unknown>) =>
			key === "pages.chat.error.categoryLabels.ModelEjected"
				? "Modell entladen"
				: t(key, defaultValue, options as never)) as unknown as TFunction;

		expect(buildInvocationSummary(summaryInput({ status: "Failed", error: " ", failureCategory: "ModelEjected" }), labels)).toBe(
			"Failed after 4.2 s with gemma3 — Modell entladen.",
		);
		expect(
			buildInvocationSummary(summaryInput({ status: "Failed", error: "Load timeout.", failureCategory: "BrandNew" }), labels),
		).toBe("Failed after 4.2 s with gemma3 — Load timeout.");
	});

	// The badge reads the localized status, never the raw wire status (COMPLETED, CANCELLED) on the German page.
	it("labels every status badge through the catalogue, with a German translation for each", () => {
		const keyOnly = ((key: string) => key) as unknown as TFunction;
		const statuses = ["Pending", "Assigned", "Running", "Completed", "Failed", "Cancelled"] as const;
		const germanStatus: Record<string, string> = de.pages.invocations.monitor.status;

		for (const status of statuses) {
			const key = getInvocationStatusBadge(summaryInput({ status }), keyOnly).label;
			expect(key).toBe(`pages.invocations.monitor.status.${status.toLowerCase()}`);
			expect(germanStatus[status.toLowerCase()]).not.toBe(status);
		}
	});

	it("leaves the duration out when a terminal run has no completion time", () => {
		const base = { durationMs: undefined, startedAt: "2026-10-06T10:00:00Z", completedAt: null };

		expect(buildInvocationSummary(summaryInput({ ...base, status: "Failed", error: "Model load timeout" }), t)).toBe(
			"Failed with gemma3 — Model load timeout.",
		);
		expect(buildInvocationSummary(summaryInput({ ...base, status: "Cancelled" }), t)).toBe("Cancelled with gemma3.");
		expect(buildInvocationSummary(summaryInput({ ...base, status: "Completed" }), t)).toBe(
			"Completed with gemma3 — 128 output chunks, 40 reasoning, no tool calls.",
		);
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
