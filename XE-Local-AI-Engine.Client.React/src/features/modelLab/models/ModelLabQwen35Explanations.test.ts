import { createInstance, type i18n as I18n } from "i18next";
import { beforeAll, describe, expect, it } from "vitest";

import de from "@/features/modelLab/locales/model-lab-de.json";
import en from "@/features/modelLab/locales/model-lab-en.json";
import { type ExplanationLevel, explanationKey, operationCandidates } from "@/features/modelLab/models/ModelLabExplanation";

// Every node id of each qwen35 template, from ModelLabQwen35Architecture.
// shortcut: copied by hand like the list in ModelLabExplanation.test.ts; the llama.cpp pin test that forces a template
// re-check is the reminder to extend it.
const qwen35Nodes = {
	linearAttention: [
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
		"attn_residual",
		"post_norm",
		"ffn_gate",
		"ffn_up",
		"ffn_act",
		"ffn_down",
		"ffn_residual",
		"control_vector",
		"output",
	],
	fullAttention: [
		"input",
		"attn_norm",
		"q",
		"k",
		"v",
		"q_norm",
		"k_norm",
		"rope",
		"attn",
		"gate",
		"gated",
		"mixer_out",
		"attn_residual",
		"post_norm",
		"ffn_gate",
		"ffn_up",
		"ffn_act",
		"ffn_down",
		"ffn_residual",
		"control_vector",
		"output",
	],
	mtp: [
		"hidden",
		"token",
		"hnorm",
		"enorm",
		"concat",
		"eh_proj",
		"attn_norm",
		"q",
		"k",
		"v",
		"q_norm",
		"k_norm",
		"rope",
		"attn",
		"gate",
		"gated",
		"mixer_out",
		"attn_residual",
		"post_norm",
		"ffn_gate",
		"ffn_up",
		"ffn_act",
		"ffn_down",
		"ffn_residual",
		"head_norm",
		"head",
		"logits",
	],
};

// ModelLabQwen35Architecture's mixture-of-experts tail (AddMixtureOfExperts), in place of the dense FFN in every template.
const denseFfn = ["ffn_gate", "ffn_up", "ffn_act", "ffn_down"];
const moeFfn = [
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
];
const qwen35moeNodes = Object.fromEntries(
	Object.entries(qwen35Nodes).map(([kind, ids]) => [
		kind,
		ids.flatMap((id) => (id === "ffn_gate" ? moeFfn : denseFfn.includes(id) ? [] : [id])),
	]),
);

const levels: ExplanationLevel[] = ["overview", "detail", "math"];

// One language only, no fallback: a text missing in German must not pass by showing the English one.
async function instanceFor(language: string, bundle: object): Promise<I18n> {
	const instance = createInstance({ lng: language, fallbackLng: false, resources: { [language]: { translation: bundle } } });
	await instance.init();
	return instance;
}

// The text a node shows, resolved the way the panel resolves it.
function shownText(
	i18n: I18n,
	kind: string,
	id: string,
	level: ExplanationLevel,
	architecture: "qwen35" | "qwen35moe" = "qwen35",
): string | null {
	const key = explanationKey(i18n, operationCandidates(kind, architecture, id), level);
	return key === null ? null : (i18n.getResource(i18n.language, "translation", key) as string);
}

const textAt = (i18n: I18n, key: string) => i18n.getResource(i18n.language, "translation", `pages.modelLab.explain.${key}`);

describe.each([
	["en", en],
	["de", de],
])("qwen35 operation texts (%s)", (language, bundle) => {
	let i18n: I18n;
	beforeAll(async () => {
		i18n = await instanceFor(language, bundle);
	});

	// The snapshot pins every text qwen35 shows, so a shared text rewritten for another family cannot change it unreviewed.
	it("show every node at every level exactly as the reviewed snapshot", async () => {
		const texts = Object.fromEntries(
			Object.entries(qwen35Nodes).flatMap(([kind, ids]) =>
				ids.flatMap((id) => levels.map((level) => [`${kind}.${id}.${level}`, shownText(i18n, kind, id, level)])),
			),
		);
		expect(Object.values(texts)).not.toContain(null);
		await expect(`${JSON.stringify(texts, null, "\t")}\n`).toMatchFileSnapshot(
			`../test/__snapshots__/ModelLabQwen35Explanations.${language}.json`,
		);
	});

	// create_tensor_qkv loads a fused attn_qkv or separate attn_q/k/v per block, and the gate is half of each head's Q rows.
	it("name both storage forms of the Q, K, V and gate texts in full attention and MTP", () => {
		for (const kind of ["fullAttention", "mtp"]) {
			for (const id of ["q", "k", "v", "gate"]) {
				for (const level of ["detail", "math"] as const) {
					expect(shownText(i18n, kind, id, level)).toContain(`\`attn_${id === "gate" ? "q" : id}\``);
					expect(shownText(i18n, kind, id, level)).toContain("`attn_qkv`");
				}
			}
			expect(shownText(i18n, kind, "q", "math")).toContain("$2 n_h d_h$");
		}
	});

	it("keep the per-kind texts ahead of qwen35's own for linear attention's output and the MTP residual", () => {
		// qwen35 has its own text for both ids, so the kind text must win over it, not only over the shared one.
		expect(i18n.exists("pages.modelLab.explain.arch.qwen35.op.mixer_out.overview")).toBe(true);
		expect(i18n.exists("pages.modelLab.explain.arch.qwen35.op.attn_residual.detail")).toBe(true);
		for (const level of levels) {
			expect(shownText(i18n, "linearAttention", "mixer_out", level)).toBe(
				i18n.getResource(language, "translation", `pages.modelLab.explain.opByKind.linearAttention.mixer_out.${level}`),
			);
			expect(shownText(i18n, "mtp", "attn_residual", level)).toBe(
				i18n.getResource(language, "translation", `pages.modelLab.explain.opByKind.mtp.attn_residual.${level}`),
			);
		}
	});
});

describe.each([
	["en", en],
	["de", de],
])("qwen35moe operation texts (%s)", (language, bundle) => {
	let i18n: I18n;
	beforeAll(async () => {
		i18n = await instanceFor(language, bundle);
	});

	it("show every node of every template at every level", () => {
		const missing = Object.entries(qwen35moeNodes).flatMap(([kind, ids]) =>
			ids.flatMap((id) =>
				levels.flatMap((level) => (shownText(i18n, kind, id, level, "qwen35moe") ? [] : [`${kind}.${id}.${level}`])),
			),
		);
		expect(missing).toEqual([]);
	});

	it("show qwen35's texts for every node the two families share", () => {
		for (const [kind, ids] of Object.entries(qwen35Nodes)) {
			for (const id of ids.filter((item) => !denseFfn.includes(item) && item !== "ffn_residual")) {
				for (const level of levels) {
					expect(shownText(i18n, kind, id, level, "qwen35moe")).toBe(shownText(i18n, kind, id, level));
				}
			}
		}
		// The fallback reaches qwen35's own overrides, not only the shared texts.
		expect(shownText(i18n, "fullAttention", "q", "detail", "qwen35moe")).toBe(textAt(i18n, "arch.qwen35.op.q.detail"));
	});

	it("add moe_out, not ffn_down, at the second residual, and renormalize the chosen weights", () => {
		for (const level of levels) {
			for (const kind of Object.keys(qwen35moeNodes)) {
				expect(shownText(i18n, kind, "ffn_residual", level, "qwen35moe")).toBe(
					textAt(i18n, `arch.qwen35moe.op.ffn_residual.${level}`),
				);
			}
			expect(shownText(i18n, "fullAttention", "topk_weights", level, "qwen35moe")).toBe(
				textAt(i18n, `arch.qwen35moe.op.topk_weights.${level}`),
			);
		}
		expect(textAt(i18n, "arch.qwen35moe.op.ffn_residual.math")).toContain("`moe_out`");
		expect(textAt(i18n, "op.ffn_residual.math")).toContain("`ffn_down`");
	});
});
