import { describe, expect, it } from "vitest";

import {
	type ComparedRun,
	commonLayerMeans,
	compareBlock,
	compareFacts,
	firstDivergence,
	pairSteeringArms,
	sameCheckpoint,
} from "@/features/modelLab/models/ModelLabComparison";

function traceRun(overrides: Partial<ComparedRun> = {}, result: ComparedRun["result"] = {}): ComparedRun {
	return {
		kind: "Trace",
		repoId: "Qwen/Qwen3.5-0.8B",
		revision: "rev-a",
		installedModelName: "org/Tiny-GGUF:Q4_K_M",
		prompt: "hello",
		appVersion: "1.0.0",
		result: {
			device: "cuda",
			torch: "2.9.0",
			transformers: "4.57.0",
			cuda: "12.8",
			layers: [3, 7],
			seed: 1234,
			maxNewTokens: 128,
			...result,
		},
		...overrides,
	};
}

function state(rows: ReturnType<typeof compareFacts>, key: string) {
	return rows.find((row) => row.key === key)?.state;
}

describe("compareFacts", () => {
	it("calls every fact the same when the runs agree, and lists the trace facts", () => {
		const rows = compareFacts([traceRun(), traceRun()]);

		expect(rows.every((row) => row.state === "same")).toBe(true);
		expect(rows.map((row) => row.key)).toEqual([
			"repo",
			"revision",
			"installedModel",
			"device",
			"torch",
			"transformers",
			"cuda",
			"appVersion",
			"seed",
			"layers",
			"maxNewTokens",
			"prompt",
		]);
		expect(rows.find((row) => row.key === "layers")?.values).toEqual(["3, 7", "3, 7"]);
	});

	it("marks each mismatch, including the prompt and a third run that alone differs", () => {
		const rows = compareFacts([traceRun(), traceRun({ revision: "rev-b" }), traceRun({ prompt: "other" }, { layers: [3] })]);

		expect(rows.filter((row) => row.state === "differs").map((row) => row.key)).toEqual(["revision", "layers", "prompt"]);
		expect(rows.find((row) => row.key === "revision")?.values).toEqual(["rev-a", "rev-b", "rev-a"]);
	});

	it("never calls a fact the same when an old run did not record it", () => {
		const old = traceRun({ appVersion: null }, { cuda: undefined, layers: undefined, seed: undefined, maxNewTokens: undefined });
		const rows = compareFacts([traceRun(), old]);

		expect(state(rows, "cuda")).toBe("unrecorded");
		expect(state(rows, "appVersion")).toBe("unrecorded");
		expect(state(rows, "seed")).toBe("unrecorded");
		expect(rows.find((row) => row.key === "cuda")?.values).toEqual(["12.8", null]);
		expect(state(rows, "repo")).toBe("same");
	});

	it("lists a difference among the runs that recorded a fact even when another did not", () => {
		const rows = compareFacts([traceRun(), traceRun({}, { cuda: "13.0" }), traceRun({}, { cuda: null })]);

		expect(state(rows, "cuda")).toBe("differs");
	});

	it("reads the steering facts and compares a vector length as shown", () => {
		const steering = (scale: number, vectorNorm: number, build: string): ComparedRun =>
			traceRun(
				{ kind: "Steering" },
				{
					steering: {
						layer: 15,
						scale,
						vectorNorm,
						seed: 7,
						arms: [
							{ name: "baseline", build },
							{ name: "vector", build },
						],
					},
				},
			);
		const rows = compareFacts([steering(1, 1.981, "b100"), steering(2, 1.979, "b200")]);

		expect(rows.map((row) => row.key)).toContain("armBuilds");
		expect(rows.map((row) => row.key)).not.toContain("layers");
		expect(state(rows, "steeringLayer")).toBe("same");
		expect(state(rows, "seed")).toBe("same");
		expect(state(rows, "vectorNorm")).toBe("same");
		expect(state(rows, "scale")).toBe("differs");
		expect(rows.find((row) => row.key === "armBuilds")?.values).toEqual(["b100", "b200"]);
		expect(state(rows, "positivePrompts")).toBe("unrecorded");
	});

	it("compares the steering contrast prompt sets", () => {
		const steering = (positivePrompts: string[]): ComparedRun =>
			traceRun({ kind: "Steering" }, { steering: { layer: 15, scale: 1, positivePrompts, negativePrompts: ["sad"] } });
		const rows = compareFacts([steering(["happy", "glad"]), steering(["happy", "joyful"])]);

		expect(state(rows, "positivePrompts")).toBe("differs");
		expect(state(rows, "negativePrompts")).toBe("same");
		expect(rows.find((row) => row.key === "positivePrompts")?.values).toEqual(["happy\nglad", "happy\njoyful"]);
	});
});

describe("compareBlock", () => {
	const run = (kind = "Trace", status = "Succeeded") => ({ kind, status });

	it("takes 2 to 4 finished runs of one kind", () => {
		expect(compareBlock([run(), run()])).toBeUndefined();
		expect(compareBlock([run(), run(), run(), run()])).toBeUndefined();
	});

	it("names why it refuses", () => {
		expect(compareBlock([run()])).toBe("count");
		expect(compareBlock([run(), run(), run(), run(), run()])).toBe("count");
		expect(compareBlock([run(), run("Steering")])).toBe("kind");
		expect(compareBlock([run(), run("Trace", "Failed")])).toBe("unfinished");
	});
});

describe("sameCheckpoint", () => {
	it("needs one repository at one revision", () => {
		expect(sameCheckpoint([traceRun(), traceRun()])).toBe(true);
		expect(sameCheckpoint([traceRun(), traceRun({ revision: "rev-b" })])).toBe(false);
		expect(sameCheckpoint([traceRun(), traceRun({ repoId: "Qwen/Other" })])).toBe(false);
	});
});

describe("firstDivergence", () => {
	it("on one checkpoint, finds the first step whose chosen tokens differ by id", () => {
		const a = [
			{ id: 1, text: "Hi" },
			{ id: 2, text: "!" },
		];
		const b = [
			{ id: 1, text: "Hi" },
			{ id: 3, text: "!" },
		];

		// Same text, different token: a difference.
		expect(firstDivergence([a, b], false)).toBe(1);
	});

	it("across checkpoints, finds a different text behind an equal id", () => {
		const a = [
			{ id: 1, text: "Hi" },
			{ id: 2, text: "!" },
		];
		const b = [
			{ id: 1, text: "Hi" },
			{ id: 2, text: "?" },
		];

		expect(firstDivergence([a, b], true)).toBe(1);
	});

	it("across checkpoints, finds none where the texts agree under different ids", () => {
		const a = [
			{ id: 1, text: "Hi" },
			{ id: 2, text: "!" },
		];
		const b = [
			{ id: 7, text: "Hi" },
			{ id: 9, text: "!" },
		];

		expect(firstDivergence([a, b], true)).toBeUndefined();
	});

	it("finds none in identical runs", () => {
		const tokens = [{ id: 1 }, { id: 2 }];

		expect(firstDivergence([tokens, [...tokens], [...tokens]], false)).toBeUndefined();
		expect(firstDivergence([[], []], false)).toBeUndefined();
	});

	it("marks where a shorter run ended", () => {
		expect(firstDivergence([[{ id: 1 }, { id: 2 }], [{ id: 1 }]], false)).toBe(1);
	});
});

describe("commonLayerMeans", () => {
	it("averages the finite norms of the blocks every run traced", () => {
		const { layers, means } = commonLayerMeans([
			[
				{ layer: 7, kind: "F", norm: 2 },
				{ layer: 3, kind: "L", norm: 1 },
				{ layer: 3, kind: "L", norm: 3 },
				{ layer: 9, kind: "L", norm: 5 },
			],
			[
				{ layer: 3, kind: "L", norm: 4 },
				{ layer: 3, kind: "L", norm: null },
				{ layer: 7, kind: "F", norm: null },
			],
		]);

		expect(layers).toEqual([
			{ index: 3, kind: "L" },
			{ index: 7, kind: "F" },
		]);
		expect(means).toEqual([
			[2, 4],
			[2, null],
		]);
	});

	it("finds no common block when the runs traced different ones", () => {
		expect(commonLayerMeans([[{ layer: 1, kind: "L", norm: 1 }], [{ layer: 2, kind: "L", norm: 1 }]])).toEqual({
			layers: [],
			means: [],
		});
	});
});

describe("pairSteeringArms", () => {
	const output = (prompt: string, text: string) => ({ prompt, text, tokenIds: [] });

	it("pairs each arm's outputs by position, with unequal lengths and differing prompts", () => {
		const arms = pairSteeringArms([
			{ arms: [{ name: "baseline", outputs: [output("Q1", "a1"), output("Q2", "a2")] }] },
			{ arms: [{ name: "baseline", outputs: [output("Q1", "b1")] }] },
			null,
		]);

		expect(arms.map((arm) => arm.name)).toEqual(["baseline", "vector", "random"]);
		expect(arms[0]?.rows).toEqual([
			{ position: 0, prompts: ["Q1", "Q1", undefined], texts: ["a1", "b1", undefined], samePrompt: false },
			{ position: 1, prompts: ["Q2", undefined, undefined], texts: ["a2", undefined, undefined], samePrompt: false },
		]);
		expect(arms[1]?.rows).toEqual([]);
	});

	it("calls a prompt the same when every run used it", () => {
		const arms = pairSteeringArms([
			{ arms: [{ name: "vector", outputs: [output("Q1", "a")] }] },
			{ arms: [{ name: "vector", outputs: [output("Q1", "b")] }] },
		]);

		expect(arms[1]?.rows[0]?.samePrompt).toBe(true);
	});
});
