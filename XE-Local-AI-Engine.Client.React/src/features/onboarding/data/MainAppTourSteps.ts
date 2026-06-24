import type { TFunction } from "i18next";
import type { Step } from "react-joyride";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";

// Stable identifiers for each tour stop, used by the provider to drive route navigation + advance-on-real-state. The
// order here IS the tour order (plan §9 happy path): point at Models → install a recommended chat model → confirm it
// is the default → open Chat → type → send → see the first response.
export const tourStepIds = [
	"navModels",
	"recommendationInstall",
	"setDefaultModel",
	"navChat",
	"chatInput",
	"chatSend",
	"firstResponse",
] as const;

export type TourStepId = (typeof tourStepIds)[number];

// Maps each step to the DOM selector it spotlights. Reuses the chat feature's existing `data-testid`s as-is (NOT
// re-attributed, plan §4) and the new `data-tour` attributes added to the nav + models surfaces. Targets are
// capability-independent (Models + Chat are always present for the default user, plan §7.4).
const stepTargets: Record<TourStepId, string> = {
	navModels: '[data-tour="nav-item-models"]',
	recommendationInstall: '[data-tour="recommendation-install"]',
	setDefaultModel: '[data-tour="set-default-model"]',
	navChat: '[data-tour="nav-item-chat"]',
	chatInput: '[data-testid="chat-input"]',
	chatSend: '[data-testid="chat-send-button"]',
	firstResponse: '[data-testid="chat-input-area"]',
};

// Builds the controlled Joyride steps from i18n keys. Every title/content resolves to an `onboarding.steps.<id>.*`
// key (asserted en/de in tests); no inline English copy. The install + send steps allow the user to interact with the
// spotlighted target (`blockTargetInteraction: false`, the v3 equivalent of v2 `spotlightClicks`) so they perform the
// real action the step describes (plan §7.2 / R1); other steps block interaction so a stray click can't desync the
// controlled tour. `skipBeacon` opens each tooltip immediately rather than showing a beacon first.
// Route-bound steps receive `targetWaitTimeout` so Joyride waits for the lazy-mounted target after navigation instead
// of immediately emitting TARGET_NOT_FOUND (plan R2).
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
			// The first response renders inside the chat input area's surrounding region; placing the tooltip above keeps
			// the spotlight clear of the message list. Other steps use Joyride's auto placement.
			placement: id === "firstResponse" ? "top" : "auto",
		} satisfies Step;
	});
}

// Steps that are bound to a specific route. The provider navigates to the route (via the router singleton) before
// advancing into these steps so it never targets an unmounted node (plan R2).
export const stepRoutes: Partial<Record<TourStepId, string>> = {
	recommendationInstall: nodeRoutePaths.modelRecommendations,
	setDefaultModel: nodeRoutePaths.models,
	chatInput: nodeRoutePaths.chat,
	chatSend: nodeRoutePaths.chat,
	firstResponse: nodeRoutePaths.chat,
};
