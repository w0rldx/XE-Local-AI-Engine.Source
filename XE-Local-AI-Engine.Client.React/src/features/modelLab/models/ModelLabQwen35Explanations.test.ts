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

const levels: ExplanationLevel[] = ["overview", "detail", "math"];

// One language only, no fallback: a text missing in German must not pass by showing the English one.
async function instanceFor(language: string, bundle: object): Promise<I18n> {
	const instance = createInstance({ lng: language, fallbackLng: false, resources: { [language]: { translation: bundle } } });
	await instance.init();
	return instance;
}

// The text a qwen35 node shows, resolved the way the panel resolves it.
function shownText(i18n: I18n, kind: string, id: string, level: ExplanationLevel): string | null {
	const key = explanationKey(i18n, operationCandidates(kind, "qwen35", id), level);
	return key === null ? null : (i18n.getResource(i18n.language, "translation", key) as string);
}

describe.each([
	["en", en],
	["de", de],
])("qwen35 operation texts (%s)", (language, bundle) => {
	let i18n: I18n;
	beforeAll(async () => {
		i18n = await instanceFor(language, bundle);
	});

	// The snapshot was written from the texts before the shared ones were rewritten for any family; qwen35 must read the same.
	it("show every node at every level exactly as before the shared texts became family-neutral", async () => {
		const texts = Object.fromEntries(
			Object.entries(qwen35Nodes).flatMap(([kind, ids]) =>
				ids.flatMap((id) => levels.map((level) => [`${kind}.${id}.${level}`, shownText(i18n, kind, id, level)])),
			),
		);
		expect(Object.values(texts)).not.toContain(null);
		await expect(`${JSON.stringify(texts, null, "\t")}\n`).toMatchFileSnapshot(
			`__snapshots__/ModelLabQwen35Explanations.${language}.json`,
		);
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
