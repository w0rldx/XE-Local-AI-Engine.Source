import type { TFunction } from "i18next";
import type { Step } from "react-joyride";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";

// Stable identifiers for each tour stop, used by the provider to drive route navigation + advance-on-real-state. The
// order here IS the tour order: point at Models → install a recommended chat model → confirm it
// is the default → open Chat → type → send → see the first response → showcase advanced features.
export const tourStepIds = [
	"navModels",
	"recommendationInstall",
	"setDefaultModel",
	"navChat",
	"chatInput",
	"chatSend",
	"firstResponse",
	// Showcase steps: static illustrative overlay (non-route-bound, non-async-real-state). Always shown regardless of
	// model capabilities — they illustrate features the user may encounter, not gates on a live model capability.
	"reasoningEffort",
	"reasoningTrace",
	"toolCall",
	"agentMode",
] as const;

export type TourStepId = (typeof tourStepIds)[number];

// The index of the first showcase step. Steps at or above this index target the always-present TourShowcasePanel
// overlay rather than a live app surface, so they require no route navigation.
export const FIRST_SHOWCASE_STEP_INDEX = tourStepIds.indexOf("reasoningEffort");

// Maps each step to the DOM selector it spotlights. Reuses the chat feature's existing `data-testid`s as-is (NOT
// re-attributed) and the new `data-tour` attributes added to the nav + models surfaces. Targets are
// capability-independent (Models + Chat are always present for the default user).
// Showcase steps target sub-sections of the TourShowcasePanel overlay (always mounted while tour is on those steps).
const stepTargets: Record<TourStepId, string> = {
	navModels: '[data-tour="nav-item-models"]',
	recommendationInstall: '[data-tour="recommendation-install"]',
	setDefaultModel: '[data-tour="set-default-model"]',
	navChat: '[data-tour="nav-item-chat"]',
	chatInput: '[data-testid="chat-input"]',
	chatSend: '[data-testid="chat-send-button"]',
	firstResponse: '[data-testid="chat-input-area"]',
	reasoningEffort: '[data-tour="showcase-reasoning-effort"]',
	reasoningTrace: '[data-tour="showcase-reasoning-trace"]',
	toolCall: '[data-tour="showcase-tool-call"]',
	agentMode: '[data-tour="showcase-agent-mode"]',
};

// Builds the controlled Joyride steps from i18n keys. Every title/content resolves to an `onboarding.steps.<id>.*`
// key (asserted en/de in tests); no inline English copy. The install + send steps allow the user to interact with the
// spotlighted target (`blockTargetInteraction: false`, the v3 equivalent of v2 `spotlightClicks`) so they perform the
// real action the step describes; other steps block interaction so a stray click can't desync the
// controlled tour. `skipBeacon` opens each tooltip immediately rather than showing a beacon first.
// Route-bound steps receive `targetWaitTimeout` so Joyride waits for the lazy-mounted target after navigation instead
// of immediately emitting TARGET_NOT_FOUND.
export function buildMainAppTourSteps(t: TFunction, targetWaitTimeoutMs = 3000): Step[] {
	return tourStepIds.map((id) => {
		const allowTargetInteraction = id === "recommendationInstall" || id === "chatSend";
		const isRouteBound = id in stepRoutes;

		return {
			target: stepTargets[id],
			title: t(`onboarding.steps.${id}.title`),
			content: t(`onboarding.steps.${id}.content`),
			skipBeacon: true,
			blockTargetInteraction: !allowTargetInteraction,
			// Route-bound steps get a wait timeout so Joyride polls for the target after the route transition completes.
			...(isRouteBound ? { targetWaitTimeout: targetWaitTimeoutMs } : {}),
			// firstResponse tooltip above input so it doesn't cover the message stream; showcase steps auto-place.
			placement: id === "firstResponse" ? "top" : "auto",
		} satisfies Step;
	});
}

// Steps that are bound to a specific route. The provider navigates to the route (via the router singleton) before
// advancing into these steps so it never targets an unmounted node.
// Showcase steps are NOT route-bound — they target the always-present TourShowcasePanel overlay.
export const stepRoutes: Partial<Record<TourStepId, string>> = {
	recommendationInstall: nodeRoutePaths.modelRecommendations,
	setDefaultModel: nodeRoutePaths.models,
	chatInput: nodeRoutePaths.chat,
	chatSend: nodeRoutePaths.chat,
	firstResponse: nodeRoutePaths.chat,
};
