import { describe, expect, it } from "vitest";

import de from "@/locales/de.json";
import en from "@/locales/en.json";

// The bundles as DATA, so no wrapper, no i18next and no dead-i18n trap — the same shape as
// ExternalAccessLocalization.test.ts. Key parity between the two locales is already gated by `src/I18n.test.ts`; what
// this file pins is the two promises the copy makes, in both languages.
describe("interface-mode localization", () => {
	it("promises in both locales that nothing is deleted and the choice can be changed later", () => {
		expect(en.pages.uiMode.setupSubtitle).toContain("Nothing is deleted");
		expect(en.pages.uiMode.setupSubtitle).toContain("Node settings");

		expect(de.pages.uiMode.setupSubtitle).toContain("nichts gelöscht");
		expect(de.pages.uiMode.setupSubtitle).toContain("Node-Einstellungen");
	});

	it("marks Simple as the recommendation in both locales", () => {
		expect(en.pages.uiMode.simple.badge).toBe("Recommended for most people");
		expect(de.pages.uiMode.simple.badge).toBe("Für die meisten empfohlen");
	});

	it("says in the settings card that every page stays reachable", () => {
		expect(en.pages.nodeSettings.uiMode.description).toContain("reachable by its own address");
		expect(de.pages.nodeSettings.uiMode.description).toContain("erreichbar");
	});
});
