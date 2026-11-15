import { describe, expect, it } from "vitest";

import { blockKindColors } from "@/core/models/inspect/GgufInspectInsights";
import de from "@/features/modelLab/locales/model-lab-de.json";
import en from "@/features/modelLab/locales/model-lab-en.json";
import { nextTokenTopics, traceMetrics } from "@/features/modelLab/models/ModelLabExplanation";

// Every component the descriptor can name: the backend's ModelLabArchitectureBuilder constants, as componentLabel lists them.
const components = ["attention", "embedding", "ffn", "mtp", "norm", "other", "output", "ssm"];

// Every node id the qwen35 templates send, from ModelLabQwen35Architecture (all three templates).
// shortcut: copied by hand, so a node added there passes here unnoticed (the panel shows no text for it); the llama.cpp
// pin test that forces a template re-check is the reminder to extend this list. Upgrade to a descriptor fixture if one appears.
const operations = [
	"input",
	"attn_norm",
	"qkv",
	"z",
	"beta",
	"alpha",
	"decay",
	"conv",
	"split",
	"l2_norm",
	"delta",
	"gated_norm",
	"mixer_out",
	"q",
	"k",
	"v",
	"q_norm",
	"k_norm",
	"rope",
	"attn",
	"gate",
	"gated",
	"attn_residual",
	"post_norm",
	"ffn_gate",
	"ffn_up",
	"ffn_act",
	"ffn_down",
	"ffn_residual",
	"control_vector",
	"output",
	"hidden",
	"token",
	"hnorm",
	"enorm",
	"concat",
	"eh_proj",
	"head_norm",
	"head",
	"logits",
];

const entries = [
	...Object.keys(blockKindColors).map((kind) => `block.${kind}`),
	...components.map((component) => `component.${component}`),
	...operations.map((id) => `op.${id}`),
	...traceMetrics.map((metric) => `trace.${metric}`),
];

const levels = ["overview", "detail", "math"];

function valueAt(bundle: unknown, path: string): unknown {
	return path
		.split(".")
		.reduce<unknown>(
			(node, key) => (typeof node === "object" && node !== null ? (node as Record<string, unknown>)[key] : undefined),
			bundle,
		);
}

function explanationStrings(value: unknown): string[] {
	return typeof value === "string"
		? [value]
		: typeof value === "object" && value !== null
			? Object.values(value).flatMap(explanationStrings)
			: [];
}

describe.each([
	["en", en],
	["de", de],
])("Model Lab explanations (%s)", (_language, bundle) => {
	it("cover every block kind, component, qwen35 operation and trace value at every level", () => {
		const missing = entries.flatMap((entry) =>
			levels.flatMap((level) => {
				const text = valueAt(bundle, `pages.modelLab.explain.${entry}.${level}`);
				return typeof text === "string" && text.trim() !== "" ? [] : [`${entry}.${level}`];
			}),
		);
		expect(missing).toEqual([]);
	});

	it("cover every Next token topic at every level", () => {
		const missing = nextTokenTopics.flatMap(({ id }) =>
			levels.flatMap((level) => {
				const text = valueAt(bundle, `pages.modelLab.nextToken.explain.${id}.${level}`);
				return typeof text === "string" && text.trim() !== "" ? [] : [`${id}.${level}`];
			}),
		);
		expect(missing).toEqual([]);
	});

	it("never hold i18next interpolation, which would eat the text between the braces", () => {
		const texts = [
			...explanationStrings(valueAt(bundle, "pages.modelLab.explain")),
			...explanationStrings(valueAt(bundle, "pages.modelLab.nextToken.explain")),
		];
		expect(texts.filter((text) => text.includes("{{"))).toEqual([]);
	});
});
