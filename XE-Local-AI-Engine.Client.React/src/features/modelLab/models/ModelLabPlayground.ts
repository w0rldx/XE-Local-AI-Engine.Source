import type { TFunction } from "i18next";
import { z } from "zod";

import { zXeLocalAiEngineClientEndpointsModelLabV1ModelLabPlaygroundConfigResponse as zConfig } from "@/core/api/generated/zod.gen";
import { formatNumber } from "@/core/formatting/NumberFormatting";

// Recorded numbers keep every bit; the page shows four significant digits and the full value on hover.
export function playgroundValue(value: number | null, t: TFunction): string {
	return value === null
		? t("pages.modelLab.playground.notFinite", "not finite")
		: formatNumber(value, { maximumSignificantDigits: 4 });
}

// A recorded number: the script writes a non-finite value as null.
const cell = z.number().nullable();
const row = z.array(cell);
const matrix = z.array(row);
// A parameter or its gradient: a bias is one row, a weight a matrix.
const tensor = z.union([matrix, row]);
const index = z.int().nonnegative();

// trace.json, formatVersion 1, as nnplayground.py writes it. Only the fields the stepper reads are kept.
export const playgroundTraceSchema = z.object({
	formatVersion: z.literal(1),
	template: z.string(),
	torchVersion: z.string(),
	config: zConfig,
	data: z.object({ count: index, probes: z.array(index), x: matrix, y: row }),
	modules: z.array(z.object({ path: z.string(), class: z.string(), outputActivation: z.string().nullable() })),
	parameters: z.array(z.object({ id: z.string(), module: z.string(), shape: z.array(index) })),
	occurrences: z.array(z.object({ module: z.string(), visit: index })),
	recorded: z.array(
		z.object({
			step: index,
			// [probe][width] at the probe rows; stream (input + output) only with residual on.
			forward: z.array(z.object({ o: index, input: matrix, output: matrix, stream: matrix.optional() })),
			loss: z.object({ value: cell, probeLogits: row, accuracy: cell }),
			// null: the loss was not finite, so no backward pass ran.
			backward: z
				.object({
					outputGrads: z.array(z.object({ o: index, grad: matrix })),
					parameterGrads: z.array(z.object({ p: index, grad: tensor })),
					// Empty unless the block is shared.
					visitGrads: z.array(z.object({ o: index, p: index, grad: tensor })),
				})
				.nullable(),
			// null: no update ran at this step.
			update: z.array(z.object({ p: index, before: tensor, after: tensor })).nullable(),
		}),
	),
	outcome: z.object({ kind: z.enum(["finished", "diverged"]), atStep: index.nullable(), updatesApplied: index }),
});
export type PlaygroundTrace = z.infer<typeof playgroundTraceSchema>;
export type PlaygroundStep = PlaygroundTrace["recorded"][number];
export type PlaygroundTensor = z.infer<typeof tensor>;

export const playgroundPhases = ["forward", "loss", "backward", "update"] as const;
export type PlaygroundPhase = (typeof playgroundPhases)[number];

// Why a phase or a check has nothing to show.
export type PlaygroundGap = "lossNotFinite" | "gradientNotFinite" | "nonFinite" | "notShared" | "shapeMismatch";

// A phase the step did not run: (a) a non-finite loss stops before backward, (b) a non-finite gradient before the update.
export function missingPhase(step: PlaygroundStep, phase: PlaygroundPhase): PlaygroundGap | null {
	if (phase === "backward" && step.backward === null) {
		return "lossNotFinite";
	}
	if (phase === "update" && step.update === null) {
		return step.backward === null ? "lossNotFinite" : "gradientNotFinite";
	}
	return null;
}

export function rows(value: PlaygroundTensor): (number | null)[][] {
	return value.map((item) => (Array.isArray(item) ? item : [item]));
}

function flat(value: PlaygroundTensor): (number | null)[] {
	return rows(value).flat();
}

function finite(values: (number | null)[]): values is number[] {
	return values.every((value) => value !== null);
}

// Same nesting and lengths, so element `at` of one is element `at` of the other.
function sameShape(a: PlaygroundTensor, b: PlaygroundTensor): boolean {
	return (
		a.length === b.length &&
		a.every((item, at) => {
			const other = b[at];
			return Array.isArray(item) ? Array.isArray(other) && item.length === other.length : !Array.isArray(other);
		})
	);
}

// A derived check: the largest residual and whether every element is within its scaled tolerance.
export type PlaygroundCheck =
	| { kind: "checked"; residual: number; withinTolerance: boolean }
	| { kind: "unavailable"; gap: PlaygroundGap };

// 1e-12 of the larger of 1 and the magnitudes involved, per element.
const relativeTolerance = 1e-12;

function check(residuals: number[], scales: number[]): PlaygroundCheck {
	return {
		kind: "checked",
		residual: Math.max(0, ...residuals),
		withinTolerance: residuals.every((residual, at) => residual <= relativeTolerance * Math.max(1, scales[at] ?? 0)),
	};
}

// SGD's update: after = before − lr·grad, per element.
export function updateCheck(step: PlaygroundStep, parameter: number, learningRate: number): PlaygroundCheck {
	const gap = missingPhase(step, "update");
	if (gap !== null) {
		return { kind: "unavailable", gap };
	}
	const update = step.update?.find((item) => item.p === parameter);
	const grad = step.backward?.parameterGrads.find((item) => item.p === parameter);
	const before = update && flat(update.before);
	const after = update && flat(update.after);
	const gradient = grad && flat(grad.grad);
	if (!(before && after && gradient && finite(before) && finite(after) && finite(gradient))) {
		return { kind: "unavailable", gap: "nonFinite" };
	}
	if (!(update && grad && sameShape(update.before, update.after) && sameShape(update.before, grad.grad))) {
		return { kind: "unavailable", gap: "shapeMismatch" };
	}
	const steps = gradient.map((value) => learningRate * value);
	return check(
		before.map((value, at) => Math.abs((after[at] ?? 0) - (value - (steps[at] ?? 0)))),
		before.map((value, at) => Math.max(Math.abs(value), Math.abs(steps[at] ?? 0))),
	);
}

// A shared block's visit gradients add up to its one parameter gradient.
export function visitSumCheck(step: PlaygroundStep, parameter: number): PlaygroundCheck {
	if (step.backward === null) {
		return { kind: "unavailable", gap: "lossNotFinite" };
	}
	const visitGrads = step.backward.visitGrads.filter((item) => item.p === parameter).map((item) => item.grad);
	if (visitGrads.length === 0) {
		return { kind: "unavailable", gap: "notShared" };
	}
	const visits = visitGrads.map(flat);
	const grad = step.backward.parameterGrads.find((item) => item.p === parameter);
	const total = grad && flat(grad.grad);
	if (!(total && finite(total) && visits.every(finite))) {
		return { kind: "unavailable", gap: "nonFinite" };
	}
	if (!(grad && visitGrads.every((visit) => sameShape(grad.grad, visit)))) {
		return { kind: "unavailable", gap: "shapeMismatch" };
	}
	const sums = total.map((_, at) => visits.reduce((sum, visit) => sum + (visit[at] ?? 0), 0));
	return check(
		total.map((value, at) => Math.abs((sums[at] ?? 0) - value)),
		total.map((_, at) => visits.reduce((sum, visit) => sum + Math.abs(visit[at] ?? 0), 0)),
	);
}
