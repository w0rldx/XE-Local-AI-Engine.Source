import { describe, expect, it } from "vitest";

import {
	missingPhase,
	type PlaygroundStep,
	playgroundTraceSchema,
	rows,
	updateCheck,
	visitSumCheck,
} from "@/features/modelLab/models/ModelLabPlayground";
import fixture from "@/features/modelLab/models/playgroundTrace.fixture.json";

// The contract fixture's run: XOR, shared block, depth 2, hidden 4, 8 steps, all recorded.
const trace = playgroundTraceSchema.parse(fixture.trace);
const learningRate = trace.config.learningRate;
const sharedParameters = trace.parameters.flatMap((parameter, p) => (parameter.module === "block" ? [p] : []));

function stepAt(index: number): PlaygroundStep {
	const step = trace.recorded[index];
	if (step === undefined) {
		throw new Error(`the fixture has no recorded step ${index}`);
	}
	return structuredClone(step);
}

describe("playgroundTraceSchema", () => {
	it("reads the contract fixture's trace", () => {
		expect(trace.recorded.map((step) => step.step)).toEqual([0, 1, 2, 3, 4, 5, 6, 7]);
		expect(trace.occurrences).toEqual([
			{ module: "embed", visit: 0 },
			{ module: "block", visit: 0 },
			{ module: "block", visit: 1 },
			{ module: "head", visit: 0 },
		]);
		expect(sharedParameters.map((p) => trace.parameters[p]?.id)).toEqual(["block.weight", "block.bias"]);
	});

	it("takes null for a non-finite number, and null for a backward pass and an update that did not run", () => {
		const step = stepAt(0);
		const diverged = {
			...fixture.trace,
			recorded: [{ ...step, loss: { ...step.loss, value: null }, backward: null, update: null }],
		};
		expect(playgroundTraceSchema.safeParse(diverged).success).toBe(true);
	});

	it.each([
		["another format version", { formatVersion: 2 }],
		["an unknown outcome", { outcome: { kind: "stopped", atStep: null, updatesApplied: 0 } }],
		["a missing occurrence list", { occurrences: undefined }],
	])("refuses %s", (_case, patch) => {
		expect(playgroundTraceSchema.safeParse({ ...fixture.trace, ...patch }).success).toBe(false);
	});
});

describe("the derived checks", () => {
	it("find every recorded update within tolerance, for every parameter", () => {
		for (const [index] of trace.recorded.entries()) {
			for (const [p] of trace.parameters.entries()) {
				expect(updateCheck(stepAt(index), p, learningRate)).toMatchObject({ kind: "checked", withinTolerance: true });
			}
		}
	});

	it("find the shared block's visit gradients adding up to its parameter gradient at every step", () => {
		for (const [index] of trace.recorded.entries()) {
			for (const p of sharedParameters) {
				expect(visitSumCheck(stepAt(index), p)).toMatchObject({ kind: "checked", withinTolerance: true });
			}
		}
	});

	it("say a parameter of a module that runs once has no visits to add up", () => {
		expect(visitSumCheck(stepAt(0), 0)).toEqual({ kind: "unavailable", gap: "notShared" });
	});

	it("catch an update that does not follow the gradient, and a visit gradient that is off", () => {
		const step = stepAt(0);
		const update = step.update?.find((item) => item.p === 5);
		if (update === undefined) {
			throw new Error("the fixture has no update of head.bias");
		}
		update.after = [Number(update.after[0]) + 1e-6];
		expect(updateCheck(step, 5, learningRate)).toMatchObject({ kind: "checked", withinTolerance: false });
		// Twice the learning rate moves every weight by one gradient more.
		expect(updateCheck(stepAt(1), 0, 2 * learningRate)).toMatchObject({ kind: "checked", withinTolerance: false });

		const visits = stepAt(0);
		const bias = visits.backward?.visitGrads.find((item) => item.p === 3);
		if (bias === undefined) {
			throw new Error("the fixture has no visit gradient of block.bias");
		}
		// block.bias is one row.
		bias.grad = rows(bias.grad)
			.flat()
			.map((value) => (value === null ? null : value * 2));
		expect(visitSumCheck(visits, 3)).toMatchObject({ kind: "checked", withinTolerance: false });
	});

	it("refuse to compare recorded values whose shapes differ", () => {
		const step = stepAt(0);
		const update = step.update?.find((item) => item.p === 5);
		const grad = step.backward?.parameterGrads.find((item) => item.p === 5);
		if (update === undefined || grad === undefined) {
			throw new Error("the fixture has no update and gradient of head.bias");
		}
		update.before = [1];
		update.after = [1];
		grad.grad = [];
		expect(updateCheck(step, 5, learningRate)).toEqual({ kind: "unavailable", gap: "shapeMismatch" });

		// A weight row cut short.
		const weights = stepAt(0);
		const weight = weights.update?.find((item) => item.p === 0);
		if (weight === undefined) {
			throw new Error("the fixture has no update of embed.weight");
		}
		weight.after = rows(weight.after).map((row, at) => (at === 0 ? row.slice(1) : row));
		expect(updateCheck(weights, 0, learningRate)).toEqual({ kind: "unavailable", gap: "shapeMismatch" });

		const visits = stepAt(0);
		const bias = visits.backward?.visitGrads.find((item) => item.p === 3);
		if (bias === undefined) {
			throw new Error("the fixture has no visit gradient of block.bias");
		}
		bias.grad = rows(bias.grad).flat().slice(1);
		expect(visitSumCheck(visits, 3)).toEqual({ kind: "unavailable", gap: "shapeMismatch" });
	});

	it("case (a): a non-finite loss leaves both later phases and both checks unavailable", () => {
		const step = { ...stepAt(0), backward: null, update: null };
		expect(missingPhase(step, "forward")).toBeNull();
		expect(missingPhase(step, "loss")).toBeNull();
		expect(missingPhase(step, "backward")).toBe("lossNotFinite");
		expect(missingPhase(step, "update")).toBe("lossNotFinite");
		expect(updateCheck(step, 2, learningRate)).toEqual({ kind: "unavailable", gap: "lossNotFinite" });
		expect(visitSumCheck(step, 2)).toEqual({ kind: "unavailable", gap: "lossNotFinite" });
	});

	it("case (b): a non-finite gradient leaves the update unavailable and its visit check without a value", () => {
		const step = { ...stepAt(0), update: null };
		const grad = step.backward?.parameterGrads.find((item) => item.p === 3);
		if (grad === undefined) {
			throw new Error("the fixture has no gradient of block.bias");
		}
		grad.grad = [null, ...rows(grad.grad).flat().slice(1)];
		expect(missingPhase(step, "backward")).toBeNull();
		expect(missingPhase(step, "update")).toBe("gradientNotFinite");
		expect(updateCheck(step, 3, learningRate)).toEqual({ kind: "unavailable", gap: "gradientNotFinite" });
		expect(visitSumCheck(step, 3)).toEqual({ kind: "unavailable", gap: "nonFinite" });
		expect(visitSumCheck(step, 2)).toMatchObject({ kind: "checked", withinTolerance: true });
	});
});
