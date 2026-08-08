import { createInstance } from "i18next";
import type { TFunction } from "i18next";
import { beforeAll, describe, expect, it } from "vitest";

import de from "@/locales/de.json";
import en from "@/locales/en.json";
import { buildMainAppTourSteps, tourStepIds } from "@/features/onboarding/data/MainAppTourSteps";

// Resolves keys against the REAL en locale (no defaultValue fallback) so a missing/mistyped key surfaces as the raw
// key string and fails the assertion below.
let t: TFunction;

beforeAll(async () => {
	const instance = createInstance({
		lng: "en",
		resources: { en: { translation: en } },
		interpolation: { escapeValue: false },
	});
	await instance.init();
	t = instance.t;
});

// Collects every leaf-key dotted path under a node — mirrors the app's I18n.test.ts parity helper so the onboarding
// subtree is compared the same way the whole locale is.
function collectKeyPaths(node: unknown, prefix = ""): string[] {
	if (node === null || typeof node !== "object" || Array.isArray(node)) {
		return [prefix];
	}
	const paths: string[] = [];
	for (const [key, value] of Object.entries(node as Record<string, unknown>)) {
		paths.push(...collectKeyPaths(value, prefix ? `${prefix}.${key}` : key));
	}
	return paths;
}

describe("mainAppTourSteps", () => {
	it("builds one step per declared step id, in order (14 total: 7 app steps + 7 showcase steps)", () => {
		const steps = buildMainAppTourSteps(t);
		expect(steps).toHaveLength(tourStepIds.length);
		expect(steps).toHaveLength(14);
	});

	it("resolves every step title and content to a real i18n key (no missing-key fallthrough)", () => {
		const steps = buildMainAppTourSteps(t);

		for (const step of steps) {
			expect(typeof step.title).toBe("string");
			expect(typeof step.content).toBe("string");
			// A missing key returns the key path itself; a resolved key returns prose that does not start with the
			// namespace prefix.
			expect(step.title as string).not.toMatch(/^onboarding\./);
			expect(step.content as string).not.toMatch(/^onboarding\./);
			expect((step.title as string).length).toBeGreaterThan(0);
			expect((step.content as string).length).toBeGreaterThan(0);
		}
	});

	it("has every step id keyed under onboarding.steps in en", () => {
		const onboardingSteps = (en as { onboarding: { steps: Record<string, unknown> } }).onboarding.steps;
		for (const id of tourStepIds) {
			expect(onboardingSteps).toHaveProperty(id);
		}
	});

	it("has identical onboarding.* keys in en and de (parity)", () => {
		const enOnboarding = (en as { onboarding: unknown }).onboarding;
		const deOnboarding = (de as { onboarding: unknown }).onboarding;

		const enKeys = collectKeyPaths(enOnboarding).sort();
		const deKeys = collectKeyPaths(deOnboarding).sort();

		expect(enKeys).toEqual(deKeys);
	});
});
