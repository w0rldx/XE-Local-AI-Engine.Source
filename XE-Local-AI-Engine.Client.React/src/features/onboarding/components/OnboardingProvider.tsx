import type { QueryClient } from "@tanstack/react-query";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { notifications } from "@mantine/notifications";
import type { ReactNode } from "react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { ACTIONS, EVENTS, Joyride, STATUS } from "react-joyride";
import type { EventData } from "react-joyride";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { router } from "@/core/integrations/tanstack-router/Router";
import { OnboardingContext } from "@/features/onboarding/context/OnboardingContext";
import { buildMainAppTourSteps, FIRST_SHOWCASE_STEP_INDEX, stepRoutes, tourStepIds } from "@/features/onboarding/data/MainAppTourSteps";
import { useTourState } from "@/features/onboarding/hooks/useTourState";
import { TourShowcasePanel } from "@/features/onboarding/components/TourShowcasePanel";
import { WelcomeTourDialog } from "@/features/onboarding/components/WelcomeTourDialog";
import {
	hasChatCapableDefault,
	hasInstalledChatModel,
	hasVisibleAssistantReply,
} from "@/features/onboarding/data/TourAdvanceSignals";

/* eslint-disable react-doctor/no-event-handler, react-doctor/no-chain-state-updates, react-doctor/no-adjust-state-on-prop-change */
// The tour advances on ASYNC external server-state (the installed-model list, the resolved default, a streamed chat
// reply) — not on synchronous user events — so reacting to that state with a useEffect is the correct React pattern
// here (there is no event handler to fold the logic into). Same suppression convention the chat/model pages use for
// their intentional effect-driven flows.

// Joyride overlay must sit above Mantine portals. The ConfirmProvider DialogShell renders at zIndex 400 and app chrome
// is below it, so 1000 keeps the spotlight + tooltip above everything the tour points at (plan §7.2 / locked decision).
const TOUR_Z_INDEX = 1000;

// How long (ms) each route-bound step waits for its target to mount after navigation (plan R2). 3 s gives the route
// lazy-load + React render cycle time to complete without flashing TARGET_NOT_FOUND on a fast box.
const TARGET_WAIT_TIMEOUT_MS = 3000;

// Index helpers so the advance-on-real-state effects don't hard-code positions (plan §7.4 happy path order).
const INSTALL_STEP_INDEX = tourStepIds.indexOf("recommendationInstall");
const DEFAULT_STEP_INDEX = tourStepIds.indexOf("setDefaultModel");
const FIRST_RESPONSE_STEP_INDEX = tourStepIds.indexOf("firstResponse");

// How many times we'll re-navigate + retry when TARGET_NOT_FOUND fires before giving up. In practice this fires only
// when the route component hasn't finished mounting; a single retry is almost always sufficient.
const MAX_TARGET_RETRIES = 2;

// Hosts the controlled Joyride tour + the opt-in welcome dialog. Owns run/stepIndex locally (single consumer — no
// Zustand, plan §9). The tour is purely additive: with this provider removed the app behaves identically (plan §3).
export function OnboardingProvider({ children }: { children: ReactNode }) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const { shouldPrompt, markDone } = useTourState();

	const [welcomeOpen, setWelcomeOpen] = useState(false);
	const [run, setRun] = useState(false);
	const [stepIndex, setStepIndex] = useState(0);
	// Once the user has answered the welcome dialog (or it was answered this session via restart) we never auto-open it
	// again — restart re-runs the tour directly, not the welcome gate.
	const promptHandledRef = useRef(false);
	// Tracks how many TARGET_NOT_FOUND retries have fired for the current step so we don't retry infinitely.
	const targetRetryCountRef = useRef(0);

	const steps = useMemo(() => buildMainAppTourSteps(t, TARGET_WAIT_TIMEOUT_MS), [t]);

	// First-run prompt: surface the welcome dialog once when the persisted state resolves with no recorded entry. Never
	// re-opens (promptHandledRef) and never blocks anything else.
	useEffect(() => {
		if (shouldPrompt && !promptHandledRef.current && !run) {
			setWelcomeOpen(true);
		}
	}, [shouldPrompt, run]);

	// Navigates to a step's bound route (via the router singleton — useNavigate is unavailable outside RouterProvider)
	// before the step renders so Joyride never targets an unmounted node (plan R2). No-op for unbound steps.
	const navigateForStep = useCallback((index: number) => {
		const id = tourStepIds[index];
		const route = id ? stepRoutes[id] : undefined;
		if (route && router.state.location.pathname !== route) {
			router.navigate({ to: route }).catch(() => undefined);
		}
	}, []);

	const goToStep = useCallback(
		(index: number) => {
			targetRetryCountRef.current = 0;
			navigateForStep(index);
			setStepIndex(index);
		},
		[navigateForStep],
	);

	const start = useCallback(() => {
		promptHandledRef.current = true;
		setWelcomeOpen(false);
		setStepIndex(0);
		targetRetryCountRef.current = 0;
		navigateForStep(0);
		setRun(true);
	}, [navigateForStep]);

	const finish = useCallback(
		(status: "completed" | "skipped") => {
			setRun(false);
			markDone(status);
		},
		[markDone],
	);

	const handleWelcomeStart = useCallback(() => start(), [start]);
	const handleWelcomeSkip = useCallback(() => {
		promptHandledRef.current = true;
		setWelcomeOpen(false);
		markDone("skipped");
	}, [markDone]);

	// Controlled-mode event handler. Joyride v3 emits onEvent(data, controls); the parent owns stepIndex and advances
	// it on the user's Next/Back. Terminal statuses persist the outcome and stop the run.
	//
	// Async real-state steps (install / default / first-response) only block FORWARD auto-advance: clicking Next on
	// them is a no-op so the tour waits for the real action (plan R1). PREV always falls through so Back still works.
	//
	// TARGET_NOT_FOUND: re-navigate and retry (up to MAX_TARGET_RETRIES) before finishing as skipped. This recovers
	// the common case where the route component hasn't mounted before Joyride checks the target selector.
	const handleEvent = useCallback(
		(data: EventData) => {
			const { type, action, index, status } = data;

			if (type === EVENTS.TOUR_END || status === STATUS.FINISHED || status === STATUS.SKIPPED) {
				finish(status === STATUS.SKIPPED || action === ACTIONS.SKIP ? "skipped" : "completed");
				return;
			}

			if (action === ACTIONS.CLOSE) {
				finish("skipped");
				return;
			}

			// TARGET_NOT_FOUND: re-navigate and try again, or give up after MAX_TARGET_RETRIES.
			if (type === EVENTS.TARGET_NOT_FOUND) {
				if (targetRetryCountRef.current < MAX_TARGET_RETRIES) {
					targetRetryCountRef.current += 1;
					// Showcase steps are NOT route-bound, so navigateForStep is a no-op for them — re-anchoring instead
					// depends on the always-mounted TourShowcasePanel. Nudge Joyride to re-measure on the next frame
					// (lets the panel finish mounting) by re-applying the current stepIndex; for route-bound steps this
					// also re-runs the navigation. rAF avoids a synchronous re-entrant setState during the event.
					navigateForStep(index);
					requestAnimationFrame(() => setStepIndex(index));
					return;
				}
				// Retries exhausted. Non-showcase (route-bound) steps finish as skipped as before.
				if (index < FIRST_SHOWCASE_STEP_INDEX) {
					finish("skipped");
					return;
				}
				// Defensive dead-end guard: showcase steps must never silently skip. The TourShowcasePanel is always
				// mounted so this should be unreachable, but if a showcase target still can't be found, force a real
				// state change — advance to the next showcase step, or finish the tour if this was the last — rather
				// than the no-op that left the screen permanently dimmed with no tooltip (the reported bug).
				if (index >= steps.length - 1) {
					finish("completed");
				} else {
					goToStep(Math.min(index + 1, steps.length - 1));
				}
				return;
			}

			if (type === EVENTS.STEP_AFTER) {
				// Async real-state steps block FORWARD-only: PREV always falls through so Back works on every step.
				const isAsyncStep =
					index === INSTALL_STEP_INDEX || index === DEFAULT_STEP_INDEX || index === FIRST_RESPONSE_STEP_INDEX;
				if (isAsyncStep && action !== ACTIONS.PREV) {
					return;
				}
				// Forward past the last step finishes the tour. In controlled mode Joyride does not emit STATUS.FINISHED
				// on its own, so without this the final step's primary button would clamp back onto itself and never persist.
				if (action !== ACTIONS.PREV && index >= steps.length - 1) {
					finish("completed");
					return;
				}
				const nextIndex = action === ACTIONS.PREV ? index - 1 : index + 1;
				goToStep(Math.max(0, Math.min(nextIndex, steps.length - 1)));
			}
		},
		[finish, goToStep, navigateForStep, steps.length],
	);

	// Real chat-model state (derived, never authoritative — plan §3). The list query is the same one ModelManagement
	// and Chat consume; reading it here only observes already-authorized state. Always enabled (not gated on `run`) so
	// the install/default effects fire as soon as server state changes — even if the tour was momentarily paused.
	const { data: modelsData } = useQuery(listLocalModelsOptions());
	const modelItems = modelsData?.items;
	const selectedModelName = modelsData?.selectedModelName;

	// R1: the install step does not advance until a chat-capable model is actually installed/selectable. When the
	// recommendations query errors or returns nothing, surface the R4 guidance note (offline / empty).
	useEffect(() => {
		if (run && stepIndex === INSTALL_STEP_INDEX && hasInstalledChatModel(modelItems)) {
			goToStep(DEFAULT_STEP_INDEX);
		}
	}, [run, stepIndex, modelItems, goToStep]);

	// R4: guidance notifications for the install step when the user is offline or recommendations are empty.
	// These are informational only — the tour stays on the install step; the user can skip any time.
	const installStepActive = run && stepIndex === INSTALL_STEP_INDEX;
	useEffect(() => {
		if (!installStepActive) {
			return;
		}
		// latestQuery error: likely offline / server unreachable.
		const latestQueryState = queryClient
			.getQueryCache()
			.findAll({ type: "active" })
			.find((q) =>
				(q.queryKey as unknown[]).some(
					(k) =>
						typeof k === "object" &&
						k !== null &&
						"_id" in k &&
						(k as Record<string, unknown>)["_id"] === "getLatestRecommendations",
				),
			);
		if (latestQueryState?.state.status === "error") {
			notifications.show({
				id: "onboarding-install-offline",
				message: t("onboarding.notes.offline"),
				color: "yellow",
				autoClose: 8000,
			});
		} else if (latestQueryState?.state.status === "success") {
			const responseData = latestQueryState.state.data as { recommendations?: unknown[] } | undefined;
			const isEmpty =
				responseData !== undefined &&
				(responseData.recommendations === undefined || responseData.recommendations.length === 0);
			if (isEmpty) {
				notifications.show({
					id: "onboarding-install-empty",
					message: t("onboarding.notes.emptyRecommendations"),
					color: "blue",
					autoClose: 8000,
				});
			}
		}
	}, [installStepActive, queryClient, t]);

	// The default-confirm step advances when a chat-capable default resolves.
	useEffect(() => {
		if (run && stepIndex === DEFAULT_STEP_INDEX && hasChatCapableDefault(modelItems, selectedModelName)) {
			goToStep(tourStepIds.indexOf("navChat"));
		}
	}, [run, stepIndex, modelItems, selectedModelName, goToStep]);

	// The send→response step advances when an assistant message is actually appended to the active conversation. Uses
	// queryClient.getQueryCache().subscribe() for event-driven notification — no polling timer (plan R1).
	// On reply: advance into the first showcase step (NOT finish) — the showcase completes the tour.
	const hasReply = useChatReplySignal(queryClient, run && stepIndex === FIRST_RESPONSE_STEP_INDEX);
	useEffect(() => {
		if (run && stepIndex === FIRST_RESPONSE_STEP_INDEX && hasReply) {
			goToStep(FIRST_SHOWCASE_STEP_INDEX);
		}
	}, [run, stepIndex, hasReply, goToStep]);

	// The showcase panel is a fixed centered overlay that exists ONLY while the tour is on a showcase step so it never
	// blocks the app at any other time (additive/non-blocking invariant, plan §3).
	const showcaseActive = run && stepIndex >= FIRST_SHOWCASE_STEP_INDEX;

	const contextValue = useMemo(() => ({ start }), [start]);

	return (
		<OnboardingContext.Provider value={contextValue}>
			<Joyride
				steps={steps}
				run={run}
				stepIndex={stepIndex}
				continuous={true}
				onEvent={handleEvent}
				options={{
					zIndex: TOUR_Z_INDEX,
					// Default buttons plus Skip so every step is skippable (plan acceptance criteria).
					buttons: ["back", "skip", "primary"],
				}}
			/>
			{/* Always mounted so the showcase `data-tour` targets exist in the DOM whenever Joyride anchors a showcase
			    step. Hidden + inert when not active (see TourShowcasePanel). Conditionally mounting it caused the tour to
			    dead-end on showcase steps: Joyride dimmed the screen but could never find the (unmounted) target. */}
			<TourShowcasePanel active={showcaseActive} />
			<WelcomeTourDialog opened={welcomeOpen} onStart={handleWelcomeStart} onSkip={handleWelcomeSkip} />
			{children}
		</OnboardingContext.Provider>
	);
}

// Subscribes to the QueryCache for any update event and recomputes `hasVisibleAssistantReply` from the cache on each
// notification. Event-driven (no timer) — satisfies "advance on real state, NOT a timer" (plan R1). Resets to false
// when the step is no longer active so stale state can't trigger a spurious finish on the next tour run.
function useChatReplySignal(queryClient: QueryClient, active: boolean): boolean {
	const [hasReply, setHasReply] = useState(false);

	useEffect(() => {
		if (!active) {
			setHasReply(false);
			return;
		}

		// Snapshot immediately in case a reply already landed before this step activated.
		setHasReply(hasVisibleAssistantReply(queryClient));

		// Subscribe to all cache events; re-evaluate on each one. The subscription is torn down when the step exits.
		const unsubscribe = queryClient.getQueryCache().subscribe(() => {
			setHasReply(hasVisibleAssistantReply(queryClient));
		});

		return unsubscribe;
	}, [active, queryClient]);

	return hasReply;
}
