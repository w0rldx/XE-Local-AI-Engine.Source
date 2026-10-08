import { describe, expect, it } from "vitest";

import { deriveContextWindow, deriveUsedContextTokens } from "@/features/chat/models/ContextUsageDerivation";
import type { ContextWindowSnapshot } from "@/features/chat/models/ContextWindowModels";

describe("context usage derivation", () => {
	it("uses the latest assistant total token count", () => {
		expect(
			deriveUsedContextTokens([
				{ role: "assistant", totalTokens: 12 },
				{ role: "user", totalTokens: 99 },
				{ role: "assistant", totalTokens: 18 },
			]),
		).toBe(18);
	});

	it("falls back to input plus output counts", () => {
		expect(deriveUsedContextTokens([{ role: "assistant", inputTokens: 10, outputTokens: 4 }])).toBe(14);
	});

	it("returns undefined until an assistant usage report exists", () => {
		expect(deriveUsedContextTokens([{ role: "user", totalTokens: 14 }, { role: "assistant" }])).toBeUndefined();
	});
});

describe("context window derivation", () => {
	const snapshot = (windowTokens: number): ContextWindowSnapshot => ({
		kind: "LastRound",
		windowTokens,
		reservedOutputTokens: 0,
		usableWindowTokens: windowTokens,
		safetyMarginTokens: 0,
		tools: [],
		toolsWithheldCount: 0,
	});

	it("returns the newest assistant snapshot", () => {
		expect(
			deriveContextWindow([
				{ role: "assistant", contextWindow: snapshot(1) },
				{ role: "user" },
				{ role: "assistant", contextWindow: snapshot(2) },
			]),
		).toEqual(snapshot(2));
	});

	it("walks past a streaming assistant turn that has no snapshot yet", () => {
		expect(
			deriveContextWindow([{ role: "assistant", contextWindow: snapshot(1) }, { role: "user" }, { role: "assistant" }]),
		).toEqual(snapshot(1));
	});

	it("returns undefined before any assistant snapshot exists", () => {
		expect(deriveContextWindow([{ role: "user" }, { role: "assistant", contextWindow: null }])).toBeUndefined();
	});
});
