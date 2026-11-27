import type {
	XeLocalAiEngineClientServicesModelLabModelLabResultToken as ResultToken,
	XeLocalAiEngineClientServicesModelLabModelLabSteeringResult as SteeringResult,
	XeLocalAiEngineClientEndpointsModelLabV1ModelLabTraceRecordResponse as TraceRecord,
} from "@/core/api/generated/types.gen";
import { formatNumber } from "@/core/formatting/NumberFormatting";
import { steeringArmOrder } from "@/features/modelLab/models/ModelLabSteering";
import type { TraceLayer } from "@/features/modelLab/models/ModelLabTrace";

// Two decimals in the UI language, ungrouped.
const twoDecimals: Intl.NumberFormatOptions = { minimumFractionDigits: 2, maximumFractionDigits: 2, useGrouping: false };
const minComparedRuns = 2;
const maxComparedRuns = 4;

// What the facts table reads from a saved run. Every reproducibility fact is optional: a run saved before it was
// recorded reads back without it.
export interface ComparedRun {
	kind: string;
	repoId: string;
	revision: string;
	installedModelName: string;
	prompt: string;
	appVersion?: string | null;
	result: {
		device?: string | null;
		torch?: string | null;
		transformers?: string | null;
		cuda?: string | null;
		layers?: number[] | null;
		seed?: number | null;
		maxNewTokens?: number | null;
		steering?: {
			layer?: number;
			scale?: number;
			vectorNorm?: number | null;
			seed?: number | null;
			positivePrompts?: string[] | null;
			negativePrompts?: string[] | null;
			arms?: { name: string; runtime?: string | null; build?: string | null }[];
		} | null;
	} | null;
}

const commonFacts = [
	"repo",
	"revision",
	"installedModel",
	"device",
	"torch",
	"transformers",
	"cuda",
	"appVersion",
	"seed",
] as const;
const traceFacts = ["layers", "maxNewTokens"] as const;
const steeringFacts = ["steeringLayer", "scale", "armBuilds", "vectorNorm", "positivePrompts", "negativePrompts"] as const;

export type FactKey = (typeof commonFacts)[number] | (typeof traceFacts)[number] | (typeof steeringFacts)[number] | "prompt";

// Same: every run recorded one value. Differs: two recorded values disagree. Unrecorded: the recorded ones agree, but
// at least one run did not record it, so the facts cannot be called the same.
export type FactState = "same" | "differs" | "unrecorded";

export interface FactRow {
	key: FactKey;
	// One per run, in the runs' order; null where the run did not record the fact.
	values: (string | null)[];
	state: FactState;
}

function text(value: string | number | null | undefined): string | null {
	return value === null || value === undefined || value === "" ? null : String(value);
}

function factValue(run: ComparedRun, key: FactKey): string | null {
	const result = run.result;
	const steering = result?.steering;
	switch (key) {
		case "repo":
			return text(run.repoId);
		case "revision":
			return text(run.revision);
		case "installedModel":
			return text(run.installedModelName);
		case "device":
			return text(result?.device);
		case "torch":
			return text(result?.torch);
		case "transformers":
			return text(result?.transformers);
		case "cuda":
			return text(result?.cuda);
		case "appVersion":
			return text(run.appVersion);
		case "seed":
			return text(run.kind === "Steering" ? steering?.seed : result?.seed);
		case "layers":
			return result?.layers ? result.layers.join(", ") : null;
		case "maxNewTokens":
			return text(result?.maxNewTokens);
		case "steeringLayer":
			return text(steering?.layer);
		case "scale":
			return text(steering?.scale);
		case "armBuilds": {
			// One entry per distinct build the arms ran on; the arms of one run normally share it.
			const builds = new Set((steering?.arms ?? []).map((arm) => text(arm.build) ?? "").filter((build) => build.length > 0));
			return builds.size > 0 ? [...builds].join(", ") : null;
		}
		case "vectorNorm":
			// Compared as shown: a difference below the shown precision is not listed.
			return steering?.vectorNorm === null || steering?.vectorNorm === undefined
				? null
				: formatNumber(steering.vectorNorm, twoDecimals);
		case "positivePrompts":
			return steering?.positivePrompts?.length ? steering.positivePrompts.join("\n") : null;
		case "negativePrompts":
			return steering?.negativePrompts?.length ? steering.negativePrompts.join("\n") : null;
		default:
			return run.prompt;
	}
}

// Every fact that could explain a difference between the runs, in display order. The runs are of one kind.
export function compareFacts(runs: ComparedRun[]): FactRow[] {
	const keys: FactKey[] = [...commonFacts, ...(runs[0]?.kind === "Steering" ? steeringFacts : traceFacts), "prompt"];
	return keys.map((key) => {
		const values = runs.map((run) => factValue(run, key));
		const recorded = values.filter((value) => value !== null);
		const state: FactState = new Set(recorded).size > 1 ? "differs" : recorded.length < values.length ? "unrecorded" : "same";
		return { key, values, state };
	});
}

export type CompareBlock = "count" | "kind" | "unfinished";

// Why the selected runs cannot be compared, or undefined when they can.
export function compareBlock(selected: { kind: string; status: string }[]): CompareBlock | undefined {
	if (selected.length < minComparedRuns || selected.length > maxComparedRuns) {
		return "count";
	}
	if (new Set(selected.map((run) => run.kind)).size > 1) {
		return "kind";
	}
	return selected.every((run) => run.status === "Succeeded") ? undefined : "unfinished";
}

// A token id names the same token only within one tokenizer, so ids compare only between runs of one checkpoint.
export function sameCheckpoint(runs: Pick<ComparedRun, "repoId" | "revision">[]): boolean {
	return new Set(runs.map((run) => `${run.repoId}@${run.revision}`)).size <= 1;
}

// The first step at which the runs' chosen tokens differ, by token id or, across checkpoints, by text; a run that ended
// earlier differs from one that went on. Undefined when every run chose the same tokens to the same length.
export function firstDivergence(runs: Pick<ResultToken, "id" | "text">[][], byText: boolean): number | undefined {
	const longest = Math.max(0, ...runs.map((tokens) => tokens.length));
	for (let step = 0; step < longest; step++) {
		const chosen = runs.map((tokens) => {
			const token = tokens[step];
			if (token === undefined) {
				return undefined;
			}
			return byText || token.id === undefined ? `text:${token.text}` : token.id;
		});
		if (new Set(chosen).size > 1) {
			return step;
		}
	}
	return undefined;
}

export interface LayerMeans {
	// The blocks every run's trace measured, in block order.
	layers: TraceLayer[];
	// One row per layer, one value per run; null where a run's records at that block held no finite norm.
	means: (number | null)[][];
}

// The mean residual norm per block per run, over the blocks every run traced. Kinds are read from the first run.
export function commonLayerMeans(runs: Pick<TraceRecord, "layer" | "kind" | "norm">[][]): LayerMeans {
	const sums = runs.map((records) => {
		const byLayer = new Map<number, { sum: number; count: number }>();
		for (const record of records) {
			const entry = byLayer.get(record.layer) ?? { sum: 0, count: 0 };
			if (record.norm !== null && Number.isFinite(record.norm)) {
				entry.sum += record.norm;
				entry.count += 1;
			}
			byLayer.set(record.layer, entry);
		}
		return byLayer;
	});
	const kinds = new Map<number, string>();
	for (const record of runs[0] ?? []) {
		kinds.set(record.layer, record.kind);
	}
	const layers = [...kinds.keys()]
		.filter((layer) => sums.every((byLayer) => byLayer.has(layer)))
		.sort((a, b) => a - b)
		.map((index) => ({ index, kind: kinds.get(index) ?? "?" }));
	const means = layers.map((layer) =>
		sums.map((byLayer) => {
			const entry = byLayer.get(layer.index);
			return entry && entry.count > 0 ? entry.sum / entry.count : null;
		}),
	);
	return { layers, means };
}

export interface ArmRow {
	position: number;
	// One per run; undefined where the run has no output at this position.
	prompts: (string | undefined)[];
	texts: (string | undefined)[];
	samePrompt: boolean;
}

// Each arm's outputs of every run, paired by prompt position, in the arms' fixed order.
export function pairSteeringArms(results: (SteeringResult | null | undefined)[]): { name: string; rows: ArmRow[] }[] {
	return steeringArmOrder.map((name) => {
		const arms = results.map((steering) => steering?.arms?.find((arm) => arm.name === name));
		const count = Math.max(0, ...arms.map((arm) => arm?.outputs.length ?? 0));
		const rows = Array.from({ length: count }, (_, position) => {
			const prompts = arms.map((arm) => arm?.outputs[position]?.prompt);
			return {
				position,
				prompts,
				texts: arms.map((arm) => arm?.outputs[position]?.text),
				samePrompt: new Set(prompts).size === 1,
			};
		});
		return { name, rows };
	});
}
