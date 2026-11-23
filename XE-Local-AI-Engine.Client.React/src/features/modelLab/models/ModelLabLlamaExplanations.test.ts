import { createInstance, type i18n as I18n } from "i18next";
import { beforeAll, describe, expect, it } from "vitest";

import de from "@/features/modelLab/locales/model-lab-de.json";
import en from "@/features/modelLab/locales/model-lab-en.json";
import { type ExplanationLevel, explanationKey, operationCandidates } from "@/features/modelLab/models/ModelLabExplanation";

// Every node id of the one llama template, from ModelLabLlamaArchitecture.
// shortcut: copied by hand like the list in ModelLabExplanation.test.ts; the llama.cpp pin test that forces a template
// re-check is the reminder to extend it.
const llamaNodes = [
	"input",
	"attn_norm",
	"q",
	"k",
	"v",
	"rope",
	"attn",
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
];

const levels: ExplanationLevel[] = ["overview", "detail", "math"];

// One language only, no fallback: a text missing in German must not pass by showing the English one.
async function instanceFor(language: string, bundle: object): Promise<I18n> {
	const instance = createInstance({ lng: language, fallbackLng: false, resources: { [language]: { translation: bundle } } });
	await instance.init();
	return instance;
}

// The text a node shows, resolved the way the panel resolves it.
function shownText(i18n: I18n, id: string, level: ExplanationLevel): string | null {
	const key = explanationKey(i18n, operationCandidates("fullAttention", "llama", id), level);
	return key === null ? null : (i18n.getResource(i18n.language, "translation", key) as string);
}

describe.each([
	["en", en],
	["de", de],
])("llama operation texts (%s)", (language, bundle) => {
	let i18n: I18n;
	beforeAll(async () => {
		i18n = await instanceFor(language, bundle);
	});

	it("show every node at every level", () => {
		const missing = llamaNodes.flatMap((id) =>
			levels.flatMap((level) => (shownText(i18n, id, level)?.trim() ? [] : [`${id}.${level}`])),
		);
		expect(missing).toEqual([]);
	});

	// create_tensor_qkv loads a fused attn_qkv or separate attn_q/k/v per block, so the shown text must hold for both.
	it("name both storage forms of the Q, K and V projections, with the optional bias", () => {
		for (const id of ["q", "k", "v"]) {
			for (const level of ["detail", "math"] as const) {
				expect(shownText(i18n, id, level)).toContain(`\`attn_${id}\``);
				expect(shownText(i18n, id, level)).toContain("`attn_qkv`");
			}
			expect(shownText(i18n, id, "math")).toContain(`+ b_${id.toUpperCase()}$$`);
		}
	});
});
