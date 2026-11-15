import { describe, expect, it } from "vitest";

import { neutralSamplerSettings, simulateSampling } from "@/features/modelLab/models/NextTokenSampling";

// Probabilities 0.5, 0.3, 0.15, 0.05 out of order, so the sort is part of every test.
const candidates = [
	{ text: "b", logprob: Math.log(0.3) },
	{ text: "a", logprob: Math.log(0.5) },
	{ text: "d", logprob: Math.log(0.05) },
	{ text: "c", logprob: Math.log(0.15) },
];

function probabilities(settings: Partial<typeof neutralSamplerSettings>): number[] {
	return simulateSampling(candidates, { ...neutralSamplerSettings, ...settings }).map((candidate) => candidate.probability);
}

describe("simulateSampling", () => {
	it("at temperature 1 with top-k and top-p off is the shown candidates renormalized, highest first", () => {
		const result = simulateSampling(candidates, neutralSamplerSettings);

		expect(result.map((candidate) => candidate.text)).toEqual(["a", "b", "c", "d"]);
		expect(result.map((candidate) => candidate.probability)).toEqual([0.5, 0.3, 0.15, 0.05].map((p) => expect.closeTo(p, 10)));
	});

	it("renormalizes over the shown candidates when they do not sum to one", () => {
		const result = simulateSampling(
			[
				{ text: "x", logprob: Math.log(0.2) },
				{ text: "y", logprob: Math.log(0.2) },
			],
			neutralSamplerSettings,
		);

		expect(result.map((candidate) => candidate.probability)).toEqual([expect.closeTo(0.5, 10), expect.closeTo(0.5, 10)]);
	});

	it("keeps each candidate's server value untouched", () => {
		const result = simulateSampling(candidates, { temperature: 0.3, topK: 2, topP: 0.5, minP: 0.1 });

		expect(result.map((candidate) => Math.exp(candidate.logprob))).toEqual(
			[0.5, 0.3, 0.15, 0.05].map((p) => expect.closeTo(p, 10)),
		);
	});

	it("at temperature 0 puts everything on the most likely candidate", () => {
		const result = simulateSampling(candidates, { ...neutralSamplerSettings, temperature: 0 });

		expect(result.map((candidate) => [candidate.probability, candidate.kept])).toEqual([
			[1, true],
			[0, false],
			[0, false],
			[0, false],
		]);
	});

	it("still marks a candidate kept when its probability underflows at a low temperature", () => {
		const result = simulateSampling(
			[
				{ text: "a", logprob: 0 },
				{ text: "b", logprob: -60 },
			],
			{ ...neutralSamplerSettings, temperature: 0.05 },
		);

		expect(result.map((candidate) => [candidate.probability, candidate.kept])).toEqual([
			[1, true],
			[0, true],
		]);
	});

	it("approaches the greedy pick as the temperature falls", () => {
		expect(probabilities({ temperature: 0.05 })[0]).toBeGreaterThan(0.99);
	});

	it("flattens the distribution above temperature 1", () => {
		// Temperature 2 takes square roots of the probabilities before renormalizing.
		const result = probabilities({ temperature: 2 });
		const roots = [0.5, 0.3, 0.15, 0.05].map(Math.sqrt);
		const total = roots.reduce((sum, root) => sum + root, 0);

		expect(result).toEqual(roots.map((root) => expect.closeTo(root / total, 10)));
		expect(result[0]).toBeLessThan(0.5);
		expect(result[3]).toBeGreaterThan(0.05);
	});

	it("with top-k 1 keeps only the first candidate", () => {
		expect(probabilities({ topK: 1 })).toEqual([1, 0, 0, 0]);
	});

	it("with top-k 2 keeps two and renormalizes them", () => {
		expect(probabilities({ topK: 2 })).toEqual([expect.closeTo(0.625, 10), expect.closeTo(0.375, 10), 0, 0]);
	});

	it.each([0, -1])("treats top-k %i as off", (topK) => {
		expect(probabilities({ topK })).toEqual([0.5, 0.3, 0.15, 0.05].map((p) => expect.closeTo(p, 10)));
	});

	it("with top-k above the shown count keeps them all", () => {
		expect(probabilities({ topK: 40 }).filter((p) => p > 0)).toHaveLength(4);
	});

	it("with top-p keeps candidates up to and including the one that reaches it", () => {
		// 0.5 + 0.3 = 0.8 reaches 0.7 at the second candidate.
		expect(probabilities({ topP: 0.7 })).toEqual([expect.closeTo(0.625, 10), expect.closeTo(0.375, 10), 0, 0]);
	});

	it.each([0, 0.1])("with top-p %d still keeps the first candidate", (topP) => {
		expect(probabilities({ topP })).toEqual([1, 0, 0, 0]);
	});

	it("cuts top-p on the untempered values, because temperature comes last", () => {
		// At temperature 3 the first two hold less than 0.7 together, but top-p ran before temperature: two are kept.
		const result = probabilities({ topP: 0.7, temperature: 3 });

		expect(result.filter((p) => p > 0)).toHaveLength(2);
		expect(result[0]).toBeLessThan(0.625);
	});

	it("applies top-p to what top-k kept, renormalized", () => {
		// After top-k 2 the first holds 0.625, which reaches top-p 0.6 alone.
		expect(probabilities({ topK: 2, topP: 0.6 })).toEqual([1, 0, 0, 0]);
	});

	it("treats min-p 0 as off", () => {
		expect(probabilities({ minP: 0 })).toEqual([0.5, 0.3, 0.15, 0.05].map((p) => expect.closeTo(p, 10)));
	});

	it("with min-p cuts candidates below min-p times the highest probability and renormalizes the rest", () => {
		// 0.25 × 0.5 = 0.125: 0.05 goes, 0.15 stays.
		expect(probabilities({ minP: 0.25 })).toEqual([
			expect.closeTo(0.5 / 0.95, 10),
			expect.closeTo(0.3 / 0.95, 10),
			expect.closeTo(0.15 / 0.95, 10),
			0,
		]);
	});

	it("with min-p 1 keeps the highest, and every candidate tied with it", () => {
		expect(probabilities({ minP: 1 })).toEqual([1, 0, 0, 0]);
		const tied = simulateSampling(
			[
				{ text: "x", logprob: Math.log(0.4) },
				{ text: "y", logprob: Math.log(0.4) },
				{ text: "z", logprob: Math.log(0.2) },
			],
			{ ...neutralSamplerSettings, minP: 1 },
		);
		expect(tied.map((candidate) => candidate.kept)).toEqual([true, true, false]);
	});

	it("applies min-p after top-p, so top-p cuts on the set before min-p", () => {
		// Top-p 0.82 first: 0.5 + 0.3 = 0.8 falls short, so three stay; min-p 0.2 (threshold 0.1) then keeps those three.
		// Run the other way round, top-p would renormalize over three and stop at two.
		expect(probabilities({ topP: 0.82, minP: 0.2 }).filter((p) => p > 0)).toHaveLength(3);
	});

	it("keeps ties in the server's order and gives the greedy pick to the first of them", () => {
		const tied = [
			{ text: "first", logprob: Math.log(0.4) },
			{ text: "second", logprob: Math.log(0.4) },
			{ text: "third", logprob: Math.log(0.2) },
		];

		const greedy = simulateSampling(tied, { ...neutralSamplerSettings, temperature: 0 });
		const neutral = simulateSampling(tied, neutralSamplerSettings);

		expect(greedy.map((candidate) => [candidate.text, candidate.probability])).toEqual([
			["first", 1],
			["second", 0],
			["third", 0],
		]);
		expect(neutral[0]?.probability).toBeCloseTo(neutral[1]?.probability ?? Number.NaN, 10);
	});

	it("handles the lowest float llama-server sends for a probability of zero", () => {
		const result = simulateSampling(
			[
				{ text: "a", logprob: -0.1 },
				{ text: "zero", logprob: -3.4028234663852886e38 },
			],
			{ ...neutralSamplerSettings, temperature: 0.05 },
		);

		expect(result.map((candidate) => candidate.probability)).toEqual([1, 0]);
	});

	it("returns nothing for no candidates", () => {
		expect(simulateSampling([], neutralSamplerSettings)).toEqual([]);
		expect(simulateSampling([], { temperature: 0, topK: 1, topP: 0.1, minP: 0.5 })).toEqual([]);
	});
});
