import { describe, expect, it } from "vitest";

import { deriveUsedContextTokens } from "@/features/chat/models/ContextUsageDerivation";

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
