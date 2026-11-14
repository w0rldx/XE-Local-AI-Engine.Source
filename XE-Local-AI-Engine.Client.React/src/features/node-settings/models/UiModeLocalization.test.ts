import { describe, expect, it } from "vitest";

import de from "@/locales/de.json";
import en from "@/locales/en.json";

// The bundles as DATA, so no wrapper, no i18next and no dead-i18n trap — the same shape as
// ExternalAccessLocalization.test.ts. Key parity between the two locales is already gated by `src/I18n.test.ts`; what
// this file pins is the promise the copy makes, in both languages.
describe("interface-mode localization", () => {
	it("says in the settings card that every page stays reachable", () => {
		expect(en.pages.nodeSettings.uiMode.description).toContain("reachable by its own address");
		expect(de.pages.nodeSettings.uiMode.description).toContain("erreichbar");
	});
});
