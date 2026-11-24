import { createInstance } from "i18next";
import { beforeAll, describe, expect, it } from "vitest";

import { zXeLocalAiEngineClientServicesModelLabModelLabArchitectureTemplate as zTemplate } from "@/core/api/generated/zod.gen";
import { blockKindColors } from "@/core/models/inspect/GgufInspectInsights";
import de from "@/features/modelLab/locales/model-lab-de.json";
import en from "@/features/modelLab/locales/model-lab-en.json";
import {
	explanationKey,
	lensTopics,
	nextTokenTopics,
	operationCandidates,
	operationLabel,
	operationLabelCandidates,
	routingTopics,
	traceMetrics,
} from "@/features/modelLab/models/ModelLabExplanation";

// Every component the descriptor can name: the backend's ModelLabArchitectureBuilder constants, as componentLabel lists them.
const components = ["attention", "embedding", "ffn", "mtp", "norm", "other", "output", "ssm"];

// Every node id the templates send: ModelLabQwen35Architecture (all three templates of both families),
// ModelLabLlamaArchitecture and ModelLabOlmoeArchitecture, whose ids all appear among qwen35's and qwen35moe's.
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
	"router",
	"router_probs",
	"topk",
	"topk_weights",
	"exp_gate",
	"exp_up",
	"exp_act",
	"exp_down",
	"exp_weighted",
	"shexp_gate",
	"shexp_up",
	"shexp_act",
	"shexp_down",
	"shexp_scale",
	"shexp_gated",
	"moe_out",
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
	...lensTopics.map((topic) => `stepper.${topic}`),
	...routingTopics.map((topic) => `stepper.${topic}`),
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
	it("cover every block kind, component, template operation, trace value, lens and routing topic at every level", () => {
		const missing = entries.flatMap((entry) =>
			levels.flatMap((level) => {
				const text = valueAt(bundle, `pages.modelLab.explain.${entry}.${level}`);
				return typeof text === "string" && text.trim() !== "" ? [] : [`${entry}.${level}`];
			}),
		);
		expect(missing).toEqual([]);
	});

	it("cover every Next token topic at every level", () => {
		const missing = nextTokenTopics.flatMap((id) =>
			levels.flatMap((level) => {
				const text = valueAt(bundle, `pages.modelLab.nextToken.explain.${id}.${level}`);
				return typeof text === "string" && text.trim() !== "" ? [] : [`${id}.${level}`];
			}),
		);
		expect(missing).toEqual([]);
	});

	it("override operations only for ids a template sends, each with text at the levels it overrides", () => {
		const architectures = valueAt(bundle, "pages.modelLab.explain.arch") as Record<string, { op: Record<string, object> }>;
		expect(Object.keys(architectures).sort()).toEqual(["llama", "olmoe", "qwen35", "qwen35moe"]);
		for (const { op } of Object.values(architectures)) {
			expect(Object.keys(op).filter((id) => !operations.includes(id))).toEqual([]);
			expect(
				Object.values(op)
					.flatMap(explanationStrings)
					.filter((text) => text.trim() === ""),
			).toEqual([]);
		}
	});

	it("write the lens norm with the effective weight γ and name it for both traced families", () => {
		const math = valueAt(bundle, "pages.modelLab.explain.stepper.lens.math") as string;
		expect(math).toContain("\\odot \\gamma$$");
		expect(math).not.toContain("\\odot (1 + w)");
		expect(math).toContain("$1 + w$");
		expect(math).toContain("OLMoE");
	});

	it("never hold i18next interpolation, which would eat the text between the braces", () => {
		const texts = [
			...explanationStrings(valueAt(bundle, "pages.modelLab.explain")),
			...explanationStrings(valueAt(bundle, "pages.modelLab.nextToken.explain")),
		];
		expect(texts.filter((text) => text.includes("{{"))).toEqual([]);
	});
});

describe("operationCandidates", () => {
	const store = createInstance({
		lng: "en",
		resources: {
			en: {
				translation: {
					pages: {
						modelLab: {
							explain: {
								opByKind: { mtp: { attn: { overview: "kind" } } },
								arch: { llama: { op: { attn: { overview: "llama", math: "llama math" } } } },
								op: { attn: { overview: "shared", detail: "shared detail", math: "shared math" } },
							},
						},
					},
				},
			},
		},
	});
	beforeAll(async () => {
		await store.init();
	});
	const shown = (kind: string, architecture: string | null, level: "overview" | "detail" | "math") =>
		explanationKey(store, operationCandidates(kind, architecture, "attn"), level);

	it("takes the kind text first, then the architecture's, then the shared one, level by level", () => {
		expect(shown("mtp", "llama", "overview")).toBe("pages.modelLab.explain.opByKind.mtp.attn.overview");
		expect(shown("fullAttention", "llama", "overview")).toBe("pages.modelLab.explain.arch.llama.op.attn.overview");
		expect(shown("fullAttention", "llama", "math")).toBe("pages.modelLab.explain.arch.llama.op.attn.math");
		// The override has no detail text, so that level falls back to the shared one.
		expect(shown("fullAttention", "llama", "detail")).toBe("pages.modelLab.explain.op.attn.detail");
		expect(shown("fullAttention", "qwen35", "overview")).toBe("pages.modelLab.explain.op.attn.overview");
		expect(shown("fullAttention", null, "overview")).toBe("pages.modelLab.explain.op.attn.overview");
	});

	it("try qwen35moe's own texts, then qwen35's, between the kind text and the shared one", () => {
		expect(operationCandidates("fullAttention", "qwen35moe", "attn")).toEqual([
			"opByKind.fullAttention.attn",
			"arch.qwen35moe.op.attn",
			"arch.qwen35.op.attn",
			"op.attn",
		]);
		// An architecture without a fallback keeps the single architecture step.
		expect(operationCandidates("fullAttention", "qwen35", "attn")).toEqual([
			"opByKind.fullAttention.attn",
			"arch.qwen35.op.attn",
			"op.attn",
		]);
	});
});

describe("operationLabel", () => {
	// The real feature bundles, in an instance of their own so the page's language stays untouched.
	const store = (lng: string) =>
		createInstance({ lng, fallbackLng: "en", resources: { de: { translation: de }, en: { translation: en } } });
	const german = store("de-DE");
	const english = store("en");
	beforeAll(async () => {
		await Promise.all([german.init(), english.init()]);
	});
	const heading = (
		i18n: typeof german,
		architecture: string | null,
		kind: string,
		qkvLayout: string | null | undefined,
		id: string,
		label: string,
	) => operationLabel(i18n, { kind, qkvLayout }, architecture, { id, label });

	it("tries qwen35moe's own keys, then qwen35's, so a MoE block gets the dense family's German heading", () => {
		// The same fallback map ModelLabOperationHeadingDriftTests copies.
		expect(operationLabelCandidates("fullAttention", "qwen35moe", "rope", null)).toEqual([
			"byKind.qwen35moe.fullAttention.rope",
			"arch.qwen35moe.rope",
			"byKind.qwen35.fullAttention.rope",
			"arch.qwen35.rope",
			"any.rope",
		]);
		expect(heading(german, "qwen35moe", "fullAttention", "separate", "rope", "MRoPE on Q and K")).toBe("MRoPE auf Q und K");
		expect(heading(german, "qwen35moe", "linearAttention", undefined, "mixer_out", "Output projection · ssm_out")).toBe(
			"Ausgabeprojektion · ssm_out",
		);
	});

	it("tries the layout suffix before the plain key, and only for a layout the backend emits", () => {
		expect(operationLabelCandidates("fullAttention", "llama", "q", "fused")).toEqual([
			"byKind.llama.fullAttention.q_fused",
			"byKind.llama.fullAttention.q",
			"arch.llama.q_fused",
			"arch.llama.q",
			"any.q_fused",
			"any.q",
		]);
		expect(operationLabelCandidates("fullAttention", "llama", "q", "split")).toEqual(
			operationLabelCandidates("fullAttention", "llama", "q", null),
		);
	});

	it("tells the two Qwen output projections apart by block kind", () => {
		expect(heading(german, "qwen35", "linearAttention", undefined, "mixer_out", "Output projection · ssm_out")).toBe(
			"Ausgabeprojektion · ssm_out",
		);
		expect(heading(german, "qwen35", "fullAttention", "separate", "mixer_out", "Output projection · attn_output")).toBe(
			"Ausgabeprojektion · attn_output",
		);
	});

	it.each([
		["qwen35", "separate", "Projektion für Query und Gate · attn_q"],
		["qwen35", "fused", "Query- und Gate-Zeilen der zusammengefassten Projektion · attn_qkv"],
		["qwen35", "mixed", "Projektion für Query und Gate · attn_q oder Q- und Gate-Zeilen von attn_qkv"],
		["llama", "separate", "Query-Projektion · attn_q"],
		["olmoe", "fused", "Query-Zeilen der zusammengefassten Projektion · attn_qkv"],
		["olmoe", "mixed", "Query-Projektion · attn_q oder Q-Zeilen von attn_qkv"],
	])("names the %s Q projection of a %s layout in German", (architecture, layout, text) => {
		expect(heading(german, architecture, "fullAttention", layout, "q", "English")).toBe(text);
	});

	it.each([
		["no", undefined],
		["a null", null],
		["an unknown", "split"],
	])("keeps the English Q/K/V heading for %s layout, rather than guess one", (_case, layout) => {
		for (const id of ["q", "k", "v"]) {
			expect(heading(german, "llama", "fullAttention", layout, id, `English ${id}`)).toBe(`English ${id}`);
		}
		expect(heading(german, "qwen35", "mtp", layout, "gate", "English gate")).toBe("English gate");
	});

	it("keeps the English heading of an id it has no German text for", () => {
		expect(heading(german, "qwen35", "fullAttention", "separate", "new_operation", "New operation")).toBe("New operation");
		expect(heading(german, null, "fullAttention", "separate", "new_operation", "New operation")).toBe("New operation");
	});

	// What the descriptors send; each tensor name, formula symbol and dotted MTP name must survive the translation.
	it.each([
		["qwen35", "linearAttention", undefined, "decay", "Decay gate: softplus(α + ssm_dt) × ssm_a"],
		["qwen35", "linearAttention", undefined, "beta", "β = sigmoid(ssm_beta · x)"],
		["qwen35", "mtp", "fused", "gate", "sigmoid(gate half of the Q rows of attn_qkv)"],
		["qwen35", "mtp", "separate", "eh_proj", "Projection · nextn.eh_proj"],
		["qwen35", "mtp", "separate", "head", "LM head · nextn.shared_head_head, else output"],
		[
			"qwen35moe",
			"fullAttention",
			"separate",
			"exp_gate",
			"Expert gate projection · ffn_gate_exps (logical half when the file stores ffn_gate_up_exps)",
		],
		["qwen35moe", "mtp", "mixed", "topk_weights", "Chosen probabilities ÷ their sum"],
		["olmoe", "fullAttention", "separate", "q_norm", "RMSNorm over the whole Q projection · attn_q_norm"],
	])("keeps the tensor names and symbols of %s %s (%s) %s in German", (architecture, kind, layout, id, label) => {
		// An empty fallback, so a missing German heading cannot pass on the English label's own tokens.
		const text = heading(german, architecture, kind, layout, id, "");
		expect(text).not.toBe("");
		for (const token of label.match(/[a-z0-9]+(?:[._][a-z0-9]+)+|[·×÷αβ]/g) ?? []) {
			expect(text).toContain(token);
		}
	});

	it("shows the descriptor's own label in English, even where German has a heading", () => {
		expect(
			heading(english, "qwen35", "fullAttention", "fused", "q", "Query + gate rows of the fused projection · attn_qkv"),
		).toBe("Query + gate rows of the fused projection · attn_qkv");
		expect(heading(english, "olmoe", "fullAttention", "separate", "input", "Block input (residual stream)")).toBe(
			"Block input (residual stream)",
		);
	});
});

describe("the generated template schema", () => {
	const template = {
		kind: "fullAttention",
		provenance: "inferred",
		source: "llama.cpp src/models/llama.cpp @ b12014",
		nodes: [{ id: "q", label: "Query projection · attn_q", component: "attention" }],
		edges: [],
	};

	it("reads a payload from before qkvLayout, and each layout the backend emits", () => {
		expect(zTemplate.parse(template).qkvLayout).toBeUndefined();
		for (const qkvLayout of ["separate", "fused", "mixed", null]) {
			expect(zTemplate.parse({ ...template, qkvLayout }).qkvLayout).toBe(qkvLayout);
		}
	});
});
