import { createInstance } from "i18next";
import type { TFunction } from "i18next";
import { beforeAll, describe, expect, it } from "vitest";

import de from "@/locales/de.json";
import en from "@/locales/en.json";
import {
	buildTutorialSteps,
	getQuickStartStepIds,
	resolveResumeStepId,
	tutorialRegistry,
} from "@/features/onboarding/data/TutorialRegistry";

let t: TFunction;

beforeAll(async () => {
	const instance = createInstance({ lng: "en", resources: { en: { translation: en } } });
	await instance.init();
	t = instance.getFixedT("en");
});

describe("tutorialRegistry", () => {
	it("contains exactly the three approved id-to-key mappings", () => {
		expect(tutorialRegistry.map(({ id, persistenceKey }) => ({ id, persistenceKey }))).toEqual([
			{ id: "quick-start", persistenceKey: "main-app-v1" },
			{ id: "agents-basics", persistenceKey: "agents-v1" },
			{ id: "knowledge-base-basics", persistenceKey: "knowledge-base-v1" },
		]);
	});

	it("builds translated steps for every tutorial in English and German", () => {
		for (const tutorial of tutorialRegistry) {
			const steps = buildTutorialSteps(t, tutorial, tutorial.stepIds);
			expect(steps).toHaveLength(tutorial.stepIds.length);
			for (const step of steps) {
				expect(step.title).not.toMatch(/^onboarding\./);
				expect(step.content).not.toMatch(/^onboarding\./);
			}
		}
		expect(Object.keys(en.onboarding.tutorials)).toEqual(Object.keys(de.onboarding.tutorials));
	});

	it("selects only setup steps required by frozen readiness", () => {
		expect(getQuickStartStepIds("ready")[0]).toBe("navChat");
		expect(getQuickStartStepIds("installed-unselected")[0]).toBe("setDefaultModel");
		expect(getQuickStartStepIds("missing")[0]).toBe("navModels");
		expect(getQuickStartStepIds("unresolved")[0]).toBe("navModels");
	});

	it("allows Quick Start actions while keeping Agents and Knowledge tutorials passive", () => {
		const quickStart = tutorialRegistry[0];
		const quickSteps = buildTutorialSteps(t, quickStart, quickStart.stepIds);
		expect(quickSteps[quickStart.stepIds.indexOf("navModels")]?.target).toBe('[data-tour="models-overview"]');
		expect(quickSteps[quickStart.stepIds.indexOf("navChat")]?.target).toBe('[data-tour="chat-overview"]');
		expect(quickSteps[quickStart.stepIds.indexOf("chatInput")]?.blockTargetInteraction).toBe(false);
		expect(quickSteps[quickStart.stepIds.indexOf("chatSend")]?.blockTargetInteraction).toBe(false);
		for (const tutorial of tutorialRegistry.slice(1)) {
			expect(buildTutorialSteps(t, tutorial, tutorial.stepIds).every((step) => step.blockTargetInteraction)).toBe(true);
		}
	});

	it("resumes at the canonical next eligible step when the saved step is no longer eligible", () => {
		const quickStart = tutorialRegistry[0];
		expect(resolveResumeStepId("recommendationInstall", quickStart.stepIds, getQuickStartStepIds("ready"))).toBe("navChat");
	});
});
