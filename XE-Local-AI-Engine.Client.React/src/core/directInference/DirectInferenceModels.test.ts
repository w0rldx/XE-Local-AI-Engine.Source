import { describe, expect, it } from "vitest";

import {
	applyDirectInferenceEvent,
	type DirectInferenceResult,
	directInferenceMaxKeptLogprobEntries,
	emptyDirectInferenceResult,
	logprobPercentLabel,
	logprobTextLabel,
} from "@/core/directInference/DirectInferenceModels";

const entry = { text: "a", logprob: -1, isReasoning: false, alternatives: [] };

describe("applyDirectInferenceEvent logprobs", () => {
	const fill = (isReasoning: boolean, count: number): DirectInferenceResult =>
		applyDirectInferenceEvent(emptyDirectInferenceResult, {
			type: "logprobs",
			logprobs: Array.from({ length: count }, () => ({ ...entry, isReasoning })),
		});

	it("keeps entries up to the ceiling and counts the rest", () => {
		const nearlyFull = fill(false, directInferenceMaxKeptLogprobEntries - 1);
		expect(nearlyFull.answerLogprobsLeftOut).toBeUndefined();

		const past = applyDirectInferenceEvent(nearlyFull, { type: "logprobs", logprobs: [entry, entry, entry] });

		expect(past.logprobs).toHaveLength(directInferenceMaxKeptLogprobEntries);
		expect(past.answerLogprobsLeftOut).toBe(2);
		expect(past.reasoningLogprobsLeftOut).toBeUndefined();
	});

	it("still keeps answer entries after reasoning filled its own quota, and counts each kind apart", () => {
		const thought = fill(true, directInferenceMaxKeptLogprobEntries + 3);
		expect(thought.reasoningLogprobsLeftOut).toBe(3);

		const answered = applyDirectInferenceEvent(thought, { type: "logprobs", logprobs: [entry] });

		expect(answered.logprobs).toHaveLength(directInferenceMaxKeptLogprobEntries + 1);
		expect(answered.logprobs?.at(-1)?.isReasoning).toBe(false);
		expect(answered.reasoningLogprobsLeftOut).toBe(3);
		expect(answered.answerLogprobsLeftOut).toBeUndefined();
	});

	it("returns the same array once the quota is full", () => {
		const full = fill(false, directInferenceMaxKeptLogprobEntries);

		const next = applyDirectInferenceEvent(full, { type: "logprobs", logprobs: [entry] });

		expect(next.logprobs).toBe(full.logprobs);
		expect(next.answerLogprobsLeftOut).toBe(1);
	});

	it("leaves the text to the deltas: entries never add to it", () => {
		const result = applyDirectInferenceEvent(
			applyDirectInferenceEvent(emptyDirectInferenceResult, { type: "delta", textDelta: "\n1" }),
			{ type: "logprobs", logprobs: [{ ...entry, text: "1" }] },
		);

		expect(result.text).toBe("\n1");
		expect(result.logprobs).toHaveLength(1);
	});
});

describe("logprob labels", () => {
	it("reads a logprob as a percentage, and one that rounds away as a bound", () => {
		expect(logprobPercentLabel(0)).toBe("100.00%");
		expect(logprobPercentLabel(-1.69)).toBe("18.45%");
		expect(logprobPercentLabel(-3.4028235e38)).toBe("<0.01%");
	});

	it("keeps whitespace visible and survives a missing text", () => {
		expect(logprobTextLabel(" the\n")).toBe('" the\\n"');
		expect(logprobTextLabel(null)).toBe('""');
	});
});
