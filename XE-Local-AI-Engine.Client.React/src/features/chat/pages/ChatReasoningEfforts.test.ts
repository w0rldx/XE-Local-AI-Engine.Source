import { describe, expect, it } from "vitest";

import type { ModelOption } from "@/features/chat/models/ChatModels";
import { resolveAvailableReasoningEfforts } from "@/features/chat/pages/ChatReasoningEfforts";

function option(overrides: Partial<ModelOption>): ModelOption {
	return { value: "model", label: "model", isAvailable: true, ...overrides };
}

describe("resolveAvailableReasoningEfforts", () => {
	it("offers the Codex vocabulary for a cloud model", () => {
		expect(resolveAvailableReasoningEfforts(option({ isCloud: true, isReasoningModel: true }))).toEqual([
			"none",
			"minimal",
			"low",
			"medium",
			"high",
			"xhigh",
			"auto",
		]);
	});

	it("offers the graded set for a model advertising the thinking capability", () => {
		expect(resolveAvailableReasoningEfforts(option({ isReasoningModel: true }))).toEqual(["none", "low", "medium", "high", "auto"]);
	});

	it("offers the binary set for a non-reasoning model and for no selection at all", () => {
		expect(resolveAvailableReasoningEfforts(option({ isReasoningModel: false }))).toEqual(["on", "none"]);
		expect(resolveAvailableReasoningEfforts(undefined)).toEqual(["on", "none"]);
	});

	it("keeps a native-reasoning model on the binary set", () => {
		expect(resolveAvailableReasoningEfforts(option({ isReasoningModel: false, isNativeReasoningModel: true }))).toEqual([
			"on",
			"none",
		]);
	});

	it("gives an external model that reasons without graded effort the binary control", () => {
		// The endpoint ignores reasoning_effort, so a none/low/medium/high menu would be four inert entries.
		const efforts = resolveAvailableReasoningEfforts(
			option({ provider: "external", isReasoningModel: true, isReasoningEffortCapable: false }),
		);

		expect(efforts).toEqual(["on", "none"]);
	});

	it("gives an external model that declared graded effort the graded selector", () => {
		const efforts = resolveAvailableReasoningEfforts(
			option({ provider: "external", isReasoningModel: true, isReasoningEffortCapable: true }),
		);

		expect(efforts).toEqual(["none", "low", "medium", "high", "auto"]);
	});

	it("treats an undeclared capability as no answer, leaving every other provider on its usual path", () => {
		// Only an external connection declares the field; a local or cloud entry reports null, which must not be read
		// as "no graded effort" and demote a thinking model to the binary control.
		expect(resolveAvailableReasoningEfforts(option({ provider: "Ollama", isReasoningModel: true }))).toEqual([
			"none",
			"low",
			"medium",
			"high",
			"auto",
		]);
	});

	// `auto` is resolved by the NODE into a concrete tier, and the node's fast tier is a graded level. A binary model
	// has no graded ladder to resolve into, so the composer must never put `auto` in front of one.
	it("does not offer auto for a binary model", () => {
		expect(resolveAvailableReasoningEfforts(option({ isReasoningModel: false }))).not.toContain("auto");
		expect(resolveAvailableReasoningEfforts(undefined)).not.toContain("auto");
		expect(
			resolveAvailableReasoningEfforts(option({ isReasoningModel: false, isNativeReasoningModel: true })),
		).not.toContain("auto");
	});
});
