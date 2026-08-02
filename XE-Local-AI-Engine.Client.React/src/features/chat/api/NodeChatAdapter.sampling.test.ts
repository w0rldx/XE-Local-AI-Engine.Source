// @vitest-environment jsdom

import { describe, expect, it } from "vitest";

// Imported at module scope on purpose. Pulling NodeChatAdapter in through `await import(...)` inside a
// test body charges its whole graph (SignalR client, axios, the generated SDK) against that test's 5s
// timeout — it measured 2.5s on a Windows packaging box and tipped over the limit under coverage
// instrumentation. At module scope the same cost lands in collection, which is not timeout-bounded.
import { toStreamRequest } from "@/features/chat/api/NodeChatAdapter";
import { toWireSamplingOptions } from "@/features/chat/models/ChatSamplingOptions";

describe("toWireSamplingOptions", () => {
	it("returns undefined when all fields are undefined", () => {
		expect(toWireSamplingOptions({})).toBeUndefined();
	});

	it("returns undefined when stop is an empty array", () => {
		expect(toWireSamplingOptions({ stop: [] })).toBeUndefined();
	});

	it("returns the set fields when at least one is provided", () => {
		const result = toWireSamplingOptions({ temperature: 0.7 });
		expect(result).toEqual({ temperature: 0.7 });
	});

	it("omits undefined fields from the result", () => {
		const result = toWireSamplingOptions({ temperature: 0.5, topP: undefined, topK: 40 });
		expect(result).toEqual({ temperature: 0.5, topK: 40 });
		expect(Object.keys(result ?? {})).not.toContain("topP");
	});

	it("includes stop sequences when non-empty", () => {
		const result = toWireSamplingOptions({ stop: ["<|end|>", "###"] });
		expect(result).toEqual({ stop: ["<|end|>", "###"] });
	});

	// Bug-reproduction: Mantine NumberInput.onChange emits strings for partial input (e.g. "05", "0.").
	// toWireSamplingOptions must coerce them to real numbers and drop non-finite values.
	it("coerces a string-typed numeric field to a real number (repro: minP stored as '05')", () => {
		const result = toWireSamplingOptions({ minP: "05" as unknown as number });
		expect(result).toBeDefined();
		expect(result?.minP).toBe(5);
		expect(typeof result?.minP).toBe("number");
	});

	it("coerces a string-typed temperature to a real number (repro: '0.2' stored as string)", () => {
		const result = toWireSamplingOptions({ temperature: "0.2" as unknown as number });
		expect(result).toBeDefined();
		expect(result?.temperature).toBe(0.2);
		expect(typeof result?.temperature).toBe("number");
	});

	it("coerces an empty string to 0 (Number('') === 0, a finite number)", () => {
		// Number("") === 0 which is finite — the wire layer keeps it as 0.
		// The dialog's onChange guard (val.trim() !== "") prevents empty strings from
		// entering the store in the first place; this is a belt-and-suspenders edge case.
		const result = toWireSamplingOptions({ temperature: "" as unknown as number });
		expect(result).toBeDefined();
		expect(result?.temperature).toBe(0);
		expect(typeof result?.temperature).toBe("number");
	});

	it("drops a numeric field that is a non-numeric string", () => {
		const result = toWireSamplingOptions({ topP: "abc" as unknown as number });
		expect(result).toBeUndefined();
	});

	it("includes all set fields together", () => {
		const result = toWireSamplingOptions({
			temperature: 0.8,
			topP: 0.9,
			topK: 50,
			minP: 0.05,
			maxOutputTokens: 512,
			repeatPenalty: 1.1,
			repeatLastN: 64,
			presencePenalty: 0.1,
			frequencyPenalty: 0.1,
			seed: 1234,
			stop: ["</s>"],
			numCtx: 4096,
		});
		expect(result).toEqual({
			temperature: 0.8,
			topP: 0.9,
			topK: 50,
			minP: 0.05,
			maxOutputTokens: 512,
			repeatPenalty: 1.1,
			repeatLastN: 64,
			presencePenalty: 0.1,
			frequencyPenalty: 0.1,
			// The seed is serialized as a precision-safe string on the wire (backend SamplingOptions.Seed contract).
			seed: "1234",
			stop: ["</s>"],
			numCtx: 4096,
		});
	});

	it("serializes the seed as a string so a large value never loses precision", () => {
		const result = toWireSamplingOptions({ seed: 987654321 });
		expect(result?.seed).toBe("987654321");
		expect(typeof result?.seed).toBe("string");
	});
});

describe("NodeChatAdapter toStreamRequest sampling forwarding", () => {
	const baseRequest = {
		conversationId: "c1",
		content: "hello",
		userMessageId: "u1",
		messageId: "m1",
		requestId: "r1",
	};

	it("forwards the wire sampling options onto the stream request", () => {
		const samplingOptions = toWireSamplingOptions({ temperature: 0.7, seed: 1234 });

		const streamRequest = toStreamRequest({ ...baseRequest, samplingOptions });

		expect(streamRequest.samplingOptions).toEqual({ temperature: 0.7, seed: "1234" });
	});

	it("omits samplingOptions entirely when none are set (developer mode off)", () => {
		const streamRequest = toStreamRequest({ ...baseRequest, samplingOptions: toWireSamplingOptions({}) });

		expect(streamRequest.samplingOptions).toBeUndefined();
	});

	it("carries the identifiers the resume registry keys on", () => {
		const streamRequest = toStreamRequest(baseRequest);

		expect(streamRequest.messageId).toBe("m1");
		expect(streamRequest.requestId).toBe("r1");
	});
});

describe("clampFieldMax context-length cap", () => {
	it("returns the field meta max for non-context-sensitive fields regardless of maxContextTokens", async () => {
		const { clampFieldMax } = await import("@/features/chat/models/ChatSamplingOptions");
		const meta = { key: "temperature" as const, labelKey: "", descriptionKey: "", min: 0, max: 2, step: 0.05, allowDecimal: true, slider: true };

		expect(clampFieldMax(meta, 4096)).toBe(2);
		expect(clampFieldMax(meta, undefined)).toBe(2);
	});

	it("clamps maxOutputTokens to maxContextTokens when context limit is smaller", async () => {
		const { clampFieldMax } = await import("@/features/chat/models/ChatSamplingOptions");
		const meta = { key: "maxOutputTokens" as const, labelKey: "", descriptionKey: "", min: 1, max: 131072, step: 128, allowDecimal: false, slider: true };

		expect(clampFieldMax(meta, 4096)).toBe(4096);
	});

	it("clamps numCtx to maxContextTokens when context limit is smaller", async () => {
		const { clampFieldMax } = await import("@/features/chat/models/ChatSamplingOptions");
		const meta = { key: "numCtx" as const, labelKey: "", descriptionKey: "", min: 512, max: 131072, step: 512, allowDecimal: false, slider: true };

		expect(clampFieldMax(meta, 8192)).toBe(8192);
	});

	it("does not clamp when maxContextTokens is larger than field meta max", async () => {
		const { clampFieldMax } = await import("@/features/chat/models/ChatSamplingOptions");
		const meta = { key: "maxOutputTokens" as const, labelKey: "", descriptionKey: "", min: 1, max: 512, step: 128, allowDecimal: false, slider: true };

		expect(clampFieldMax(meta, 131072)).toBe(512);
	});

	it("returns field meta max when maxContextTokens is undefined", async () => {
		const { clampFieldMax } = await import("@/features/chat/models/ChatSamplingOptions");
		const meta = { key: "maxOutputTokens" as const, labelKey: "", descriptionKey: "", min: 1, max: 131072, step: 128, allowDecimal: false, slider: true };

		expect(clampFieldMax(meta, undefined)).toBe(131072);
	});
});
