import { describe, expect, it } from "vitest";

import de from "@/locales/de.json";
import en from "@/locales/en.json";

// The bundles as DATA, so no wrapper, no i18next and no dead-i18n trap. `de` is asserted this way rather than through
// a render because `src/i18n.ts` bundles only `en` statically and loads `de` as a lazy chunk. Key parity between the
// two is already gated by `src/I18n.test.ts`, so it is deliberately not retested here.
describe("external-access localization", () => {
	it("states the three disabled checks and the three unblocked services in both locales", () => {
		const disablesEn = en.pages.externalAccess.offline.disables;
		expect(disablesEn).toContain("application update checks");
		expect(disablesEn).toContain("llama.cpp runtime update checks");
		expect(disablesEn).toContain("the first-run model and runtime download");
		expect(disablesEn).toContain("You can still start every one of them yourself, at any time.");

		// One key, two surfaces: the first-run Offline card and the Node Settings external-access card both render it.
		const doesNotBlockEn = en.pages.externalAccess.offline.doesNotBlock;
		expect(doesNotBlockEn).toContain("This is not a network switch.");
		expect(doesNotBlockEn).toContain("the C0re platform");
		expect(doesNotBlockEn).toContain("MCP servers you have configured");
		expect(doesNotBlockEn).toContain("model-catalog lookups");

		const disablesDe = de.pages.externalAccess.offline.disables;
		expect(disablesDe).toContain("die Suche nach Anwendungs-Updates");
		expect(disablesDe).toContain("die Suche nach Updates der llama.cpp-Laufzeit");
		expect(disablesDe).toContain("den Download von Modell und Laufzeit beim ersten Start");
		expect(disablesDe).toContain("Sie können jedes davon jederzeit selbst starten.");

		const doesNotBlockDe = de.pages.externalAccess.offline.doesNotBlock;
		expect(doesNotBlockDe).toContain("Dies schaltet nicht den gesamten Netzwerkzugriff ab.");
		expect(doesNotBlockDe).toContain("C0re-Plattform");
		expect(doesNotBlockDe).toContain("MCP-Server");
		expect(doesNotBlockDe).toContain("Abfragen des Modellkatalogs");
	});

	it("never overclaims the offline profile in either locale", () => {
		// Overclaiming a privacy guarantee is a misrepresentation, and worse than shipping no switch at all. The German
		// sentence is a negation ("schaltet nicht … ab"), which is the honest form, not one of these.
		const englishSubtree = JSON.stringify(en.pages.externalAccess);
		expect(englishSubtree).not.toMatch(/no external connections|fully offline|airgapped|air-gap/i);

		const germanSubtree = JSON.stringify(de.pages.externalAccess);
		expect(germanSubtree).not.toMatch(
			/vollständig offline|komplett offline|keine externen Verbindungen|keine Internetverbindung|air-gap|airgapped/i,
		);
	});

	it("keeps the elapsed-time placeholder intact in both locales", () => {
		// A dropped interpolation variable is a silent defect: the counter would render the literal placeholder text.
		expect(en.pages.chat.loadingModelElapsed).toContain("{{elapsed}}");
		expect(de.pages.chat.loadingModelElapsed).toContain("{{elapsed}}");
	});
});
