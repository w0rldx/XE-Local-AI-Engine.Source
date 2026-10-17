import { describe, expect, it } from "vitest";

import de from "@/locales/de.json";
import en from "@/locales/en.json";

const headlineCategories = ["InsufficientCapacity", "ModelLoadFailed", "ModelUnavailable", "Timeout"];
const labelCategories = [
	"InsufficientCapacity",
	"ModelLoadFailed",
	"ModelNotInstalled",
	"ModelUnavailable",
	"ProviderAuthFailed",
	"ProviderRateLimited",
	"ProviderUnreachable",
	"Timeout",
];

describe("chat failure category keys", () => {
	it.each([
		["en", en],
		["de", de],
	])("%s carries every headline, label and the shared provider text", (_, bundle) => {
		const error = bundle.pages.chat.error;
		expect(Object.keys(error.categories).sort()).toEqual(headlineCategories);
		expect(Object.keys(error.categoryLabels).sort()).toEqual(labelCategories);
		expect(error.providerError.length).toBeGreaterThan(0);
	});
});
