import type { TFunction } from "i18next";
import { describe, expect, it } from "vitest";

import { turnThroughputLabel } from "@/features/chat/models/ChatTurnThroughput";

// Interpolates the fallback text, so the assertions read the line a user sees without the app's i18n instance.
const t = ((_key: string, fallback: string, options: { value: string }) =>
	fallback.replace("{{value}}", options.value)) as unknown as TFunction;

describe("turnThroughputLabel", () => {
	it("renders time to first token, the decode rate and the cached prompt tokens", () => {
		expect(
			turnThroughputLabel(
				{ timeToFirstTokenMs: 1_400, generationTokens: 150, generationMs: 2_000, cachedPromptTokens: 2_080 },
				t,
				"en",
			),
		).toBe("1.4 s to first token · 75 tok/s · 2,080 cached");
	});

	it("keeps one decimal under 10 tok/s and formats numbers in the reader's language", () => {
		expect(
			turnThroughputLabel(
				{ timeToFirstTokenMs: 12_345, generationTokens: 19, generationMs: 2_000, cachedPromptTokens: 2_080 },
				t,
				"de",
			),
		).toBe("12,3 s to first token · 9,5 tok/s · 2.080 cached");
	});

	it("leaves out a part whose numbers are missing, and returns nothing when no part remains", () => {
		expect(
			turnThroughputLabel({ timeToFirstTokenMs: 800, generationTokens: 150, generationMs: null, cachedPromptTokens: 0 }, t, "en"),
		).toBe("0.8 s to first token");
		expect(turnThroughputLabel({ generationTokens: 150, generationMs: 1_000 }, t, "en")).toBe("150 tok/s");
		expect(turnThroughputLabel({ timeToFirstTokenMs: null, generationTokens: null }, t, "en")).toBeUndefined();
		expect(turnThroughputLabel(undefined, t, "en")).toBeUndefined();
	});
});
