import i18next from "i18next";
import { beforeAll, describe, expect, it } from "vitest";

import { type LicenseFacts, licenseConfirmationLabel } from "@/features/modelLab/models/ModelLabLicense";
import { loadModelLabStrings } from "@/features/modelLab/models/ModelLabStrings";
import { i18nReady } from "@/i18n";

const confirmEn = "I confirm I am permitted to download these weights and to run experiments on them.";
const confirmDe = "Ich bestätige, dass ich diese Gewichte herunterladen und Experimente damit durchführen darf.";

// `en` is the server's text for the same facts (LicenseGateService.BuildModelLabText), word for word.
const variants: { name: string; facts: LicenseFacts; en: string; de: string }[] = [
	{
		name: "no license metadata",
		facts: { repoId: "org/A", license: null, isGated: false, metadataPresent: false },
		en: `The base checkpoint 'org/A' has no license metadata found for it. ${confirmEn}`,
		de: `Für den Basis-Checkpoint „org/A“ wurden keine Lizenz-Metadaten gefunden. ${confirmDe}`,
	},
	{
		name: "no license declared",
		facts: { repoId: "org/A", license: "", isGated: false, metadataPresent: true },
		en: `The base checkpoint 'org/A' declares no license. ${confirmEn}`,
		de: `Der Basis-Checkpoint „org/A“ deklariert keine Lizenz. ${confirmDe}`,
	},
	{
		name: "a declared license on a gated repository",
		facts: { repoId: "org/A", license: "llama3", isGated: true, metadataPresent: true },
		en: `The base checkpoint 'org/A' declares the license 'llama3'. The repository is gated and its terms were accepted on Hugging Face. ${confirmEn}`,
		de: `Der Basis-Checkpoint „org/A“ deklariert die Lizenz „llama3“. Das Repository ist zugangsbeschränkt, und seine Bedingungen wurden auf Hugging Face akzeptiert. ${confirmDe}`,
	},
];

describe("licenseConfirmationLabel", () => {
	beforeAll(async () => {
		await i18nReady;
		await Promise.all([loadModelLabStrings("en"), loadModelLabStrings("de")]);
	});

	it.each(variants)("says $name in English as the server records it, and in German", ({ facts, en, de }) => {
		expect(licenseConfirmationLabel(i18next.getFixedT("en"), facts)).toBe(en);
		expect(licenseConfirmationLabel(i18next.getFixedT("de"), facts)).toBe(de);
	});
});
