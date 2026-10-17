import { createInstance } from "i18next";
import type { TFunction } from "i18next";
import { beforeAll, describe, expect, it } from "vitest";

import { formatFailureCategory } from "@/features/integrations/components/IntegrationFormatters";
import de from "@/locales/de.json";
import en from "@/locales/en.json";

// The backend vocabulary, IntegrationFailureCategories.All. IntegrationFailureCategoryLabelTests (backend) fails when the
// locale labels and that set drift apart; update this list with it.
const categories = [
	"trigger-unavailable",
	"cloud-model-rejected",
	"capacity-rejected",
	"restart",
	"queue-full",
	"shutdown",
	"internal-failure",
	"approval-required",
	"queue-timeout",
	"session-policy",
];

let tEn: TFunction;
let tDe: TFunction;

beforeAll(async () => {
	const instance = createInstance({ lng: "en", resources: { en: { translation: en }, de: { translation: de } } });
	await instance.init();
	tEn = instance.getFixedT("en");
	tDe = instance.getFixedT("de");
});

describe("formatFailureCategory", () => {
	it("localizes a known category instead of printing the wire code", () => {
		expect(formatFailureCategory(tDe, "cloud-model-rejected")).toBe("Cloud-Modell abgelehnt");
		expect(formatFailureCategory(tEn, "cloud-model-rejected")).toBe("Cloud model refused");
	});

	it("labels every backend category in both locales", () => {
		for (const category of categories) {
			expect(formatFailureCategory(tEn, category)).not.toBe(category);
			expect(formatFailureCategory(tDe, category)).not.toBe(category);
		}
	});

	it("renders a category this client does not know as the raw value", () => {
		expect(formatFailureCategory(tDe, "a-category-this-client-has-never-seen")).toBe("a-category-this-client-has-never-seen");
	});
});

describe("the triggers intro", () => {
	it("names graph workflows as trigger targets, not only agents", () => {
		expect(formatIntro(tEn)).toContain("graph workflow");
		expect(formatIntro(tDe)).toContain("Graph-Workflow");
	});
});

function formatIntro(t: TFunction): string {
	return t("pages.integrations.triggers.subtitle");
}
