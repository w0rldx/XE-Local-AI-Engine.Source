import i18next from "i18next";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import de from "@/features/modelLab/locales/model-lab-de.json";
import en from "@/features/modelLab/locales/model-lab-en.json";
import { loadModelLabStrings } from "@/features/modelLab/models/ModelLabStrings";
import { i18nReady } from "@/i18n";

function keyPaths(value: unknown, prefix = ""): string[] {
	return typeof value === "object" && value !== null
		? Object.entries(value).flatMap(([key, child]) => keyPaths(child, prefix ? `${prefix}.${key}` : key))
		: [prefix];
}

describe("Model Lab's lazy strings", () => {
	beforeAll(async () => {
		await i18nReady;
	});

	afterAll(() => i18next.changeLanguage("en"));

	it("hold the same keys in both languages", () => {
		expect(keyPaths(de)).toEqual(keyPaths(en));
	});

	it("reach the store only when asked, and in German when the page is German", async () => {
		expect(i18next.exists("pages.modelLab.steer.armRandom", { lng: "de", fallbackLng: false })).toBe(false);

		await loadModelLabStrings("de-DE");

		expect(i18next.t("pages.modelLab.steer.armRandom", { lng: "de" })).toBe("Zufallskontrolle");
		expect(loadModelLabStrings("de-DE")).toBe(loadModelLabStrings("de-DE"));
	});

	it("say in German what the prompt tokens and the tab links do", async () => {
		await loadModelLabStrings("de-DE");

		expect(i18next.t("pages.modelLab.nextToken.ui.promptTokens.notLoaded", { lng: "de" })).toBe(
			"Das Modell wurde entladen oder sein Server beendet, bevor seine Prompt-Tokens gelesen werden konnten. Starten Sie den Lauf erneut, um sie zu sehen.",
		);
		expect(i18next.t("pages.modelLab.trace.showInArchitecture", { lng: "de", index: 3 })).toBe(
			"Block 3 unter Architektur zeigen",
		);
		expect(i18next.t("pages.modelLab.architecture.openTrace", { lng: "de" })).toBe("Trace öffnen");
	});
});
