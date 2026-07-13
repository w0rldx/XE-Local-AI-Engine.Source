import type { QueryClient } from "@tanstack/react-query";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { notifications } from "@mantine/notifications";
import type { ReactNode } from "react";
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import { ACTIONS, EVENTS, Joyride, STATUS } from "react-joyride";
import type { EventData } from "react-joyride";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { router } from "@/core/integrations/tanstack-router/Router";
import { OnboardingContext } from "@/features/onboarding/context/OnboardingContext";
import { buildMainAppTourSteps, FIRST_SHOWCASE_STEP_INDEX, stepRoutes, tourStepIds } from "@/features/onboarding/data/MainAppTourSteps";
import { clearTourProgress, readTourProgress, useTourState, writeTourProgress } from "@/features/onboarding/hooks/useTourState";
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
// is below it, so 1000 keeps the spotlight + tooltip above everything the tour points at.
const TOUR_Z_INDEX = 1000;

// How long (ms) each route-bound step waits for its target to mount after navigation. 3 s gives the route
// lazy-load + React render cycle time to complete without flashing TARGET_NOT_FOUND on a fast box.
const TARGET_WAIT_TIMEOUT_MS = 3000;

// Index helpers so the advance-on-real-state effects don't hard-code positions.
const INSTALL_STEP_INDEX = tourStepIds.indexOf("recommendationInstall");
const DEFAULT_STEP_INDEX = tourStepIds.indexOf("setDefaultModel");
const FIRST_RESPONSE_STEP_INDEX = tourStepIds.indexOf("firstResponse");

// How many times we'll re-navigate + re-measure when TARGET_NOT_FOUND fires before we ADVANCE past the step. This
// fires when a route component hasn't finished mounting OR when a legit target is still loading (e.g. the
// recommendations list is fetching on first run). A few rAF-spaced retries give a slow-but-real target a chance to
// appear before we move on, while the finite cap guarantees we never spin forever. On exhaustion we ADVANCE (never
// dead-end) — see handleEvent (Bug A).
const MAX_TARGET_RETRIES = 4;

// Hosts the controlled Joyride tour + the opt-in welcome dialog. Owns run/stepIndex locally (single consumer — no
// Zustand). The tour is purely additive: with this provider removed the app behaves identically.
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
	// Gates the async real-state auto-advance (install / default / first-response). It must fire ONLY when the step's
	// condition transitions unmet→met WHILE the user sits on the step (i.e. they performed the action), NEVER when the
	// condition was already satisfied the moment they arrived. Without this, a returning/test user who already has a
	// model installed + a default set would see the next step render for ~1s and then auto-advance on its own — the
	// step flashes and is skipped before it can be read (user-reported flash-then-skip bug). We arm this ref only after
	// observing the condition UNMET (work still to do); a step that starts already-met leaves it false so auto-advance
	// stays disabled and the user advances manually with Next. Reset to false on every step change (goToStep/start).
	const autoAdvanceArmedRef = useRef(false);

	const steps = useMemo(() => buildMainAppTourSteps(t, TARGET_WAIT_TIMEOUT_MS), [t]);

	// Joyride's nav-button labels default to hardcoded English; without an explicit `locale` they stay English even when
	// the app is in German. Derive them from i18next so they follow the selected language. react-i18next rebinds `t` on
	// every language change, so depending on `t` (same convention as the `steps` memo above) recomputes the labels on a
	// live language switch (the welcome-screen picker). Keys match the react-joyride v3.1 Locale shape (back, close,
	// last, next, nextWithProgress, open, skip).
	const locale = useMemo(
		() => ({
			back: t("onboarding.controls.back"),
			close: t("onboarding.controls.close"),
			last: t("onboarding.controls.last"),
			next: t("onboarding.controls.next"),
			nextWithProgress: t("onboarding.controls.nextWithProgress"),
			open: t("onboarding.controls.open"),
			skip: t("onboarding.controls.skip"),
		}),
		[t],
	);

	// Navigates to a step's bound route (via the router singleton — useNavigate is unavailable outside RouterProvider)
	// before the step renders so Joyride never targets an unmounted node. No-op for unbound steps.
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
			// Each step begins disarmed: the new step's own effect re-arms auto-advance only if it observes the condition
			// still unmet, so a step that arrives already-met never flash-skips (see autoAdvanceArmedRef).
			autoAdvanceArmedRef.current = false;
			navigateForStep(index);
			setStepIndex(index);
			// Persist progress so a mid-tour reload resumes here (Bug B). Cleared on finish().
			writeTourProgress(index);
		},
		[navigateForStep],
	);

	const start = useCallback(
		(index = 0) => {
			promptHandledRef.current = true;
			setWelcomeOpen(false);
			setStepIndex(index);
			targetRetryCountRef.current = 0;
			// Start (and resume-at-saved-index) lands on a fresh step disarmed — see autoAdvanceArmedRef. A resumed user
			// whose condition is already met must read the step, not have it flash-skip.
			autoAdvanceArmedRef.current = false;
			navigateForStep(index);
			writeTourProgress(index);
			setRun(true);
		},
		[navigateForStep],
	);

	const finish = useCallback(
		(status: "completed" | "skipped") => {
			setRun(false);
			// Always clear persisted progress so a completed/skipped tour can never resurrect on the next reload (Bug B).
			clearTourProgress();
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

	// First-run prompt / resume gate: runs once the persisted state resolves with no recorded TERMINAL entry. If a
	// mid-tour progress index was persisted to localStorage before a reload (Bug B), RESUME the tour at that index
	// instead of showing the Welcome dialog; otherwise surface the welcome dialog. Never re-opens / re-resumes
	// (promptHandledRef) and never blocks anything else. A persisted index out of range for the current step array is
	// treated as no progress (defensive — e.g. a tour-length change between releases). Declared after start/finish so it
	// can call start() without hitting a temporal-dead-zone on the memoized callback.
	useEffect(() => {
		if (!shouldPrompt || promptHandledRef.current || run) {
			return;
		}
		const savedIndex = readTourProgress();
		if (savedIndex !== null && savedIndex < steps.length) {
			start(savedIndex);
			return;
		}
		setWelcomeOpen(true);
	}, [shouldPrompt, run, steps.length, start]);

	// Controlled-mode event handler. Joyride v3 emits onEvent(data, controls); the parent owns stepIndex and advances
	// it on the user's Next/Back. Terminal statuses persist the outcome and stop the run.
	//
	// Async real-state steps (install / default / first-response) only block FORWARD auto-advance: clicking Next on
	// them is a no-op so the tour waits for the real action. PREV always falls through so Back still works.
	//
	// TARGET_NOT_FOUND: re-navigate and re-measure (up to MAX_TARGET_RETRIES) then ADVANCE past the step — never
	// dead-end (Bug A). This recovers the common case where the route component hasn't mounted (or the data hasn't
	// finished fetching) before Joyride checks the target selector.
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

			// TARGET_NOT_FOUND: re-navigate + re-measure for a few frames, then ADVANCE past the step. NO step (route-bound
			// or showcase) is allowed to dead-end — the old code silently finish("skipped") for route-bound steps, which
			// left the screen grayed for seconds then ended the tour (Bug A). Unified recovery: every missing target ends
			// up advancing to the next step, or finishing the tour cleanly if it was the last.
			if (type === EVENTS.TARGET_NOT_FOUND) {
				if (targetRetryCountRef.current < MAX_TARGET_RETRIES) {
					targetRetryCountRef.current += 1;
					// Showcase steps are NOT route-bound, so navigateForStep is a no-op for them — re-anchoring instead
					// depends on the always-mounted TourShowcasePanel. Route-bound steps re-run the navigation here so a
					// slow lazy route gets another chance. Nudge Joyride to re-measure on the next frame (lets the target
					// finish mounting / the data finish fetching) by re-applying the current stepIndex. rAF avoids a
					// synchronous re-entrant setState during the event and naturally spaces retries across frames.
					navigateForStep(index);
					requestAnimationFrame(() => setStepIndex(index));
					return;
				}
				// Retries exhausted: ADVANCE rather than dead-end (applies to ALL steps — route-bound and showcase). If
				// this was the last step, finish the tour as completed; otherwise step forward. goToStep re-navigates +
				// resets the retry counter so the next step gets its own fresh retry budget, and the overlay clears as the
				// new step renders (no flicker — the dim only persisted before because we never moved off the missing step).
				if (index >= steps.length - 1) {
					finish("completed");
				} else {
					goToStep(Math.min(index + 1, steps.length - 1));
				}
				return;
			}

			if (type === EVENTS.STEP_AFTER) {
				// Next ALWAYS advances — including on the async real-state steps (install / default / first-response). Those
				// steps still auto-advance via the effects below when their real action completes, but a manual Next must never
				// be a no-op: in controlled Joyride a blocked Next ends the step lifecycle and hides the tooltip, stranding a
				// grayed overlay with no way forward (user-reported dead-end on a fresh node with no recommendations). Letting
				// Next continue keeps the tour navigable on every environment; installing a model simply fast-forwards the
				// relevant step before the user gets there. PREV always falls through too, so Back works on every step.
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

	// Real chat-model state (derived, never authoritative). The list query is the same one ModelManagement
	// and Chat consume; reading it here only observes already-authorized state. Not gated on `run` so the
	// install/default effects fire as soon as server state changes — even if the tour was momentarily paused. It IS
	// gated on auth: this provider mounts above the RouterProvider (outside route-level session restore), so without
	// the gate every authenticated hard navigation fires this before the token is restored → a guaranteed 401 →
	// refresh → retry. Mirrors useTourState / GgufDownloadPoller.
	const isAuthenticated = useNodeAuthStore((state) => Boolean(state.accessToken));
	const { data: modelsData } = useQuery({ ...listLocalModelsOptions(), enabled: isAuthenticated });
	const modelItems = modelsData?.items;
	const selectedModelName = modelsData?.selectedModelName;

	// The install step does not advance until a chat-capable model is actually installed/selectable. We only
	// auto-advance on a genuine unmet→met transition (the user installs a model WHILE on this step). Wait for the list
	// to resolve (modelItems !== undefined) before deciding so a still-loading list neither arms nor advances. If no
	// chat model is installed yet, arm transition detection (there is work to do); once one appears we advance only if
	// armed — a user who arrived already having a model installed leaves it disarmed and reads the step instead of
	// watching it flash past (see autoAdvanceArmedRef). When the recommendations query errors or returns nothing, the
	// guidance note below surfaces (offline / empty).
	useEffect(() => {
		if (!run || stepIndex !== INSTALL_STEP_INDEX || modelItems === undefined) {
			return;
		}
		if (!hasInstalledChatModel(modelItems)) {
			autoAdvanceArmedRef.current = true;
			return;
		}
		if (autoAdvanceArmedRef.current) {
			goToStep(DEFAULT_STEP_INDEX);
		}
	}, [run, stepIndex, modelItems, goToStep]);

	// Guidance notifications for the install step when the user is offline or recommendations are empty.
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

	// The default-confirm step advances when a chat-capable default resolves — but only on a genuine unmet→met
	// transition (the user picks the default WHILE on this step), never when one was already set on arrival (that would
	// flash the step and skip it). Wait for the list to resolve first. When NO chat model is installed the
	// skip-if-prereq-missing recovery effect below owns forward motion, so we bail here (arming/auto-advance is only
	// meaningful once a model exists to set as default). With a model installed but no chat-capable default yet, arm
	// transition detection; once a default appears, advance only if armed (see autoAdvanceArmedRef).
	useEffect(() => {
		if (!run || stepIndex !== DEFAULT_STEP_INDEX || modelItems === undefined || !hasInstalledChatModel(modelItems)) {
			return;
		}
		if (!hasChatCapableDefault(modelItems, selectedModelName)) {
			autoAdvanceArmedRef.current = true;
			return;
		}
		if (autoAdvanceArmedRef.current) {
			goToStep(tourStepIds.indexOf("navChat"));
		}
	}, [run, stepIndex, modelItems, selectedModelName, goToStep]);

	// Skip-if-prereq-missing (Bug A polish): the `setDefaultModel` step spotlights a model-row control that only exists
	// once a chat model is installed. Normally the prior install step gates forward-advance on exactly that, so a model
	// IS installed by the time we arrive here. But if we land on this step with the installed-models list resolved and
	// EMPTY (e.g. the user removed the model, or recovery advanced us in unexpectedly), proactively step forward instead
	// of letting Joyride dim the screen for the full target-wait timeout before TARGET_NOT_FOUND fires. We require the
	// list to be resolved (modelItems !== undefined) so a still-loading list doesn't trigger a premature skip.
	useEffect(() => {
		if (run && stepIndex === DEFAULT_STEP_INDEX && modelItems !== undefined && !hasInstalledChatModel(modelItems)) {
			goToStep(tourStepIds.indexOf("navChat"));
		}
	}, [run, stepIndex, modelItems, goToStep]);

	// The send→response step advances when an assistant message is actually appended to the active conversation. Uses
	// queryClient.getQueryCache().subscribe() for event-driven notification — no polling timer.
	// On reply: advance into the first showcase step (NOT finish) — the showcase completes the tour.
	const hasReply = useChatReplySignal(queryClient, run && stepIndex === FIRST_RESPONSE_STEP_INDEX);
	useEffect(() => {
		if (!run || stepIndex !== FIRST_RESPONSE_STEP_INDEX) {
			return;
		}
		// The reply signal is already event-driven (no still-loading ambiguity), so gating on the active step is enough.
		// No reply yet → arm so the first streamed reply advances us; a reply already present on arrival (returning user)
		// leaves it disarmed so the step stays visible to be read instead of flashing past (see autoAdvanceArmedRef).
		if (!hasReply) {
			autoAdvanceArmedRef.current = true;
			return;
		}
		if (autoAdvanceArmedRef.current) {
			goToStep(FIRST_SHOWCASE_STEP_INDEX);
		}
	}, [run, stepIndex, hasReply, goToStep]);

	// The showcase panel is a fixed centered overlay that exists ONLY while the tour is on a showcase step so it never
	// blocks the app at any other time (additive/non-blocking invariant).
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
				// Localized nav-button labels (back / next / skip / finish / close) so they follow the selected UI language.
				// `locale` is a top-level shared prop in react-joyride v3.1 (not part of `options`); see the `locale` memo.
				locale={locale}
				options={{
					zIndex: TOUR_Z_INDEX,
					// Default buttons plus Skip so every step is skippable.
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

// Derives `hasVisibleAssistantReply` directly from the QueryCache (an external store) via useSyncExternalStore, so the
// value is recomputed on every cache event without copying it into local state (no derived-state effect / cascading
// setState). Event-driven (no timer) — advances on real state, not a timer. When the step is not
// active the snapshot is always false, so stale state can't trigger a spurious finish on the next tour run, and the
// subscription is a no-op (nothing re-renders this hook on cache events while inactive).
function useChatReplySignal(queryClient: QueryClient, active: boolean): boolean {
	const subscribe = useCallback(
		(onStoreChange: () => void) => {
			if (!active) {
				return () => undefined;
			}
			return queryClient.getQueryCache().subscribe(onStoreChange);
		},
		[active, queryClient],
	);

	const getSnapshot = useCallback(
		() => (active ? hasVisibleAssistantReply(queryClient) : false),
		[active, queryClient],
	);

	return useSyncExternalStore(subscribe, getSnapshot);
}
