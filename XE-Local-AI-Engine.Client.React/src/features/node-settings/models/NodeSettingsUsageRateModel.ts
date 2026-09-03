import { z } from "zod";
import type {
	XeLocalAiEngineClientServicesNodeSettingsModelRate as ModelRate,
	XeLocalAiEngineClientEndpointsNodeSettingsV1NodeSettingsResponse as NodeSettingsResponse,
} from "@/core/api/generated";

// biome-ignore lint/suspicious/noControlCharactersInRegex: Node settings reject wire-unsafe control characters.
const controlCharPattern = /[\u0000-\u001f]/;

// Per-model USD rates keep numeric inputs as `number | string` until save. Ordered editor rows reduce into the
// model-keyed storage map.
export interface UsageRateRow {
	// Client-only stable key for the row editor (React list key + focus stability). Never sent to the backend — the
	// build step reduces rows into the keyed rate map and drops this.
	readonly id: string;
	readonly modelName: string;
	readonly inputPer1M: number | string;
	readonly outputPer1M: number | string;
}

// Generates a stable id for a rate row. `crypto.randomUUID` is available in every supported browser and in the jsdom/
// Node test runtime; a timestamp+random fallback keeps it from throwing in any exotic environment.
function newRateRowId(): string {
	return typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
		? crypto.randomUUID()
		: `rate-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

// A fresh, empty rate row for the "add rate" affordance.
export function newUsageRateRow(): UsageRateRow {
	return { id: newRateRowId(), modelName: "", inputPer1M: "", outputPer1M: "" };
}

// True when a rate cell holds no usable value (empty / whitespace-only string). A numeric 0 is a real, valid rate.
function isRateCellEmpty(value: number | string): boolean {
	return typeof value === "string" && value.trim().length === 0;
}

// Resolves a rate cell to a finite, non-negative number, or undefined when empty / negative / non-numeric. Note an
// empty string must NOT coerce to 0 (JS `Number("") === 0`), so the empty check runs first.
function toValidRate(value: number | string): number | undefined {
	if (isRateCellEmpty(value)) {
		return undefined;
	}
	const numeric = typeof value === "number" ? value : Number(value);
	if (!Number.isFinite(numeric) || numeric < 0) {
		return undefined;
	}
	return numeric;
}

// A model-name key for a rate row: trimmed, non-empty, no control characters (mirrors the tool-capable name rule).
const rateModelNameSchema = z
	.string()
	.trim()
	.min(1)
	.refine((value) => !controlCharPattern.test(value), { message: "control-char" });

// Validates the rate rows into the wire map. A fully-empty row (blank name AND both cells empty) is silently dropped so
// a freshly-added-but-unfilled row never blocks a save. Any other malformed row (missing name, negative/non-numeric
// rate, control chars) sets `hasInvalid`. Duplicate model names collapse (last row wins). An empty result maps to null
// so the caller can clear the stored table (null-preserving on the backend).
export function validateUsageRates(rows: readonly UsageRateRow[]): {
	map: Record<string, ModelRate> | null;
	hasInvalid: boolean;
} {
	const map: Record<string, ModelRate> = {};
	let hasInvalid = false;

	for (const row of rows) {
		const nameEmpty = row.modelName.trim().length === 0;
		if (nameEmpty && isRateCellEmpty(row.inputPer1M) && isRateCellEmpty(row.outputPer1M)) {
			continue;
		}
		const parsedName = rateModelNameSchema.safeParse(row.modelName);
		const input = toValidRate(row.inputPer1M);
		const output = toValidRate(row.outputPer1M);
		if (!parsedName.success || input === undefined || output === undefined) {
			hasInvalid = true;
			continue;
		}
		map[parsedName.data] = { inputPer1M: input, outputPer1M: output };
	}

	return { map: Object.keys(map).length > 0 ? map : null, hasInvalid };
}

// Canonical string for a rate map so change detection is order-independent (map keys sorted by model name).
export function canonicalRateMap(map: Record<string, ModelRate> | null): string {
	if (map === null) {
		return "null";
	}
	const sorted = Object.entries(map).sort(([a], [b]) => a.localeCompare(b));
	return JSON.stringify(sorted);
}

// Maps the GET response's usageRates map into editor rows, sorted by model name for a stable render. Absent/null → [].
export function toUsageRateRows(map: NodeSettingsResponse["usageRates"]): UsageRateRow[] {
	if (!map) {
		return [];
	}
	return Object.entries(map)
		.map(([modelName, rate]) => ({
			id: newRateRowId(),
			modelName,
			inputPer1M: rate.inputPer1M ?? "",
			outputPer1M: rate.outputPer1M ?? "",
		}))
		.sort((a, b) => a.modelName.localeCompare(b.modelName));
}
