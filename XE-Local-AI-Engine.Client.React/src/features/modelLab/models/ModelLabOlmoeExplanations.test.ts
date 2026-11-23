import { createInstance, type i18n as I18n } from "i18next";
import { beforeAll, describe, expect, it } from "vitest";

import de from "@/features/modelLab/locales/model-lab-de.json";
import en from "@/features/modelLab/locales/model-lab-en.json";
import { type ExplanationLevel, explanationKey, operationCandidates } from "@/features/modelLab/models/ModelLabExplanation";

// Every node id of the one olmoe template, from ModelLabOlmoeArchitecture.
// shortcut: copied by hand like the list in ModelLabExplanation.test.ts; the llama.cpp pin test that forces a template
// re-check is the reminder to extend it.
const olmoeNodes = [
	"input",
	"attn_norm",
	"q",
	"k",
	"v",
	"q_norm",
	"k_norm",
	"rope",
	"attn",
	"mixer_out",
	"attn_residual",
	"post_norm",
	"router",
	"router_probs",
	"topk",
	"topk_weights",
	"exp_gate",
	"exp_up",
	"exp_act",
	"exp_down",
	"exp_weighted",
	"ffn_residual",
	"control_vector",
	"output",
];

const levels: ExplanationLevel[] = ["overview", "detail", "math"];

// The ids whose shared text is false or undecided for OLMoE, with the levels overridden. Most at every level, so no wrong level
// shows through; q, k and v only where the shared text names the separate tensor, which a fused attn_qkv file does not have.
const projection: ExplanationLevel[] = ["detail", "math"];
const overridden: Record<string, ExplanationLevel[]> = {
	q: projection,
	k: projection,
	v: projection,
	q_norm: levels,
	k_norm: levels,
	rope: levels,
	attn: levels,
	topk_weights: levels,
	exp_gate: levels,
	exp_up: levels,
	exp_down: levels,
	ffn_residual: levels,
};

// One language only, no fallback: a text missing in German must not pass by showing the English one.
async function instanceFor(language: string, bundle: object): Promise<I18n> {
	const instance = createInstance({ lng: language, fallbackLng: false, resources: { [language]: { translation: bundle } } });
	await instance.init();
	return instance;
}

// The text a node shows, resolved the way the panel resolves it.
function shownText(i18n: I18n, id: string, level: ExplanationLevel): string | null {
	const key = explanationKey(i18n, operationCandidates("fullAttention", "olmoe", id), level);
	return key === null ? null : (i18n.getResource(i18n.language, "translation", key) as string);
}

const textAt = (i18n: I18n, key: string) =>
	i18n.getResource(i18n.language, "translation", `pages.modelLab.explain.${key}`) as string | undefined;

describe.each([
	["en", en],
	["de", de],
])("olmoe operation texts (%s)", (language, bundle) => {
	let i18n: I18n;
	beforeAll(async () => {
		i18n = await instanceFor(language, bundle);
	});

	it("show every node at every level", () => {
		const missing = olmoeNodes.flatMap((id) =>
			levels.flatMap((level) => (shownText(i18n, id, level)?.trim() ? [] : [`${id}.${level}`])),
		);
		expect(missing).toEqual([]);
	});

	it("override exactly the audited ids and levels, each with its own text instead of the shared one", () => {
		const own = i18n.getResource(language, "translation", "pages.modelLab.explain.arch.olmoe.op") as Record<string, object>;
		expect(Object.keys(own).sort()).toEqual(Object.keys(overridden).sort());
		for (const [id, overriddenLevels] of Object.entries(overridden)) {
			expect(Object.keys(own[id] ?? {}).sort()).toEqual([...overriddenLevels].sort());
			for (const level of overriddenLevels) {
				expect(shownText(i18n, id, level)).toBe(textAt(i18n, `arch.olmoe.op.${id}.${level}`));
				expect(shownText(i18n, id, level)).not.toBe(textAt(i18n, `op.${id}.${level}`));
			}
		}
	});

	it("name both storage forms of the Q, K and V projections", () => {
		for (const id of ["q", "k", "v"]) {
			for (const level of projection) {
				expect(textAt(i18n, `arch.olmoe.op.${id}.${level}`)).toContain(`\`attn_${id}\``);
				expect(textAt(i18n, `arch.olmoe.op.${id}.${level}`)).toContain("`attn_qkv`");
			}
			// build_qkv adds the optional bias before the q/k norm.
			expect(textAt(i18n, `arch.olmoe.op.${id}.math`)).toContain(`+ b_${id.toUpperCase()}$$`);
		}
	});

	it("keep the chosen weights unnormalized and add the routed sum, not ffn_down, at the second residual", () => {
		expect(textAt(i18n, "arch.olmoe.op.topk_weights.math")).toContain("w_e = p_e,");
		expect(textAt(i18n, "arch.olmoe.op.topk_weights.detail")).toContain("`norm_w = false`");
		expect(textAt(i18n, "arch.olmoe.op.ffn_residual.math")).toContain("y_{\\text{routed}}");
		expect(textAt(i18n, "arch.olmoe.op.ffn_residual.math")).not.toContain("ffn_down");
		expect(textAt(i18n, "arch.olmoe.op.q_norm.math")).toContain("n_h d_h");
	});

	it("carry a layout note", () => {
		const note = i18n.getResource(language, "translation", "pages.modelLab.architecture.layoutNote.olmoe") as string;
		expect(note).toContain("PyTorch");
		expect(note).toContain("GGUF");
	});
});
