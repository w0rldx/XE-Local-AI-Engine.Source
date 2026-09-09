import { useQueryClient } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { EventData } from "react-joyride";
import { ACTIONS, EVENTS, Joyride, STATUS } from "react-joyride";

import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { router } from "@/core/integrations/tanstack-router/Router";
import { WelcomeTourDialog } from "@/features/onboarding/components/WelcomeTourDialog";
import { OnboardingContext } from "@/features/onboarding/context/OnboardingContext";
import { hasChatCapableDefault, hasInstalledChatModel } from "@/features/onboarding/data/TourAdvanceSignals";
import {
	buildTutorialSteps,
	getTutorialDefinition,
	resolveResumeStepId,
	type TutorialId,
} from "@/features/onboarding/data/TutorialRegistry";
import { useQuickStartReadiness } from "@/features/onboarding/hooks/useQuickStartReadiness";
import { clearTutorialProgress, readTutorialProgress, writeTutorialProgress } from "@/features/onboarding/hooks/useTourState";
import { useTutorialProgress } from "@/features/onboarding/hooks/useTutorialProgress";
import { useVisibleAssistantReplyCount } from "@/features/onboarding/hooks/useVisibleAssistantReplyCount";

const TOUR_Z_INDEX = 1000;
const TARGET_WAIT_TIMEOUT_MS = 3000;
const MAX_TARGET_RETRIES = 4;

/* eslint-disable react-doctor/no-adjust-state-on-prop-change, react-doctor/no-chain-state-updates -- the optional welcome invitation and real milestone advancement react to authenticated backend/cache state. */

interface ActiveTutorial {
	tutorialId: TutorialId;
	stepIds: readonly string[];
	stepId: string;
}

export function OnboardingProvider({ children }: { children: ReactNode }) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const progress = useTutorialProgress();
	const isAuthenticated = useNodeAuthStore((state) => Boolean(state.accessToken));
	const { modelItems, selectedModelName, eligibleStepIds } = useQuickStartReadiness(isAuthenticated);
	const [active, setActive] = useState<ActiveTutorial | null>(null);
	const [welcomeOpen, setWelcomeOpen] = useState(false);
	const welcomeHandledRef = useRef(false);
	const targetRetryCountRef = useRef(0);
	const autoAdvanceArmedRef = useRef(false);
	const replyCountBaselineRef = useRef<number | null>(null);

	const navigateToStep = useCallback((tutorialId: TutorialId, stepId: string) => {
		const route = getTutorialDefinition(tutorialId).routeByStepId[stepId];
		if (route && router.state.location.pathname !== route) {
			router.navigate({ to: route }).catch(() => undefined);
		}
	}, []);

	const activate = useCallback(
		(tutorialId: TutorialId, mode: "start" | "resume" | "restart") => {
			if (active !== null) {
				return;
			}
			const definition = getTutorialDefinition(tutorialId);
			if (!definition.isAvailable) {
				return;
			}
			const frozenStepIds = eligibleStepIds(tutorialId);
			let stepId = frozenStepIds[0];
			if (!stepId) {
				return;
			}
			if (mode === "resume") {
				const progress = readTutorialProgress(definition.persistenceKey, definition.stepIds);
				if (progress) {
					stepId = resolveResumeStepId(progress.stepId, definition.stepIds, frozenStepIds);
				}
			} else {
				clearTutorialProgress(definition.persistenceKey);
			}
			welcomeHandledRef.current = true;
			setWelcomeOpen(false);
			targetRetryCountRef.current = 0;
			autoAdvanceArmedRef.current = false;
			replyCountBaselineRef.current = null;
			navigateToStep(tutorialId, stepId);
			writeTutorialProgress(definition.persistenceKey, stepId);
			progress.refreshProgress();
			setActive({ tutorialId, stepIds: frozenStepIds, stepId });
		},
		[active, eligibleStepIds, navigateToStep, progress.refreshProgress],
	);

	const finish = useCallback(
		(status: "completed" | "skipped") => {
			if (!active) {
				return;
			}
			progress.markTerminal(active.tutorialId, status);
			autoAdvanceArmedRef.current = false;
			replyCountBaselineRef.current = null;
			setActive(null);
			progress.refreshProgress();
		},
		[active, progress.markTerminal, progress.refreshProgress],
	);

	const dismiss = useCallback(
		(tutorialId: TutorialId) => {
			progress.markTerminal(tutorialId, "skipped");
			if (tutorialId === "quick-start") {
				welcomeHandledRef.current = true;
				setWelcomeOpen(false);
			}
			progress.refreshProgress();
		},
		[progress.markTerminal, progress.refreshProgress],
	);

	const goToStep = useCallback(
		(stepId: string) => {
			if (!active) {
				return;
			}
			let relevantStepId = stepId;
			if (active.tutorialId === "quick-start" && modelItems !== undefined) {
				const hasInstalledModel = hasInstalledChatModel(modelItems);
				const hasDefaultModel = hasChatCapableDefault(modelItems, selectedModelName);
				if (stepId === "recommendationInstall" && hasInstalledModel) {
					relevantStepId = hasDefaultModel ? "navChat" : "setDefaultModel";
				} else if (stepId === "setDefaultModel" && hasDefaultModel) {
					relevantStepId = "navChat";
				}
				if (!active.stepIds.includes(relevantStepId)) {
					relevantStepId = stepId;
				}
			}
			targetRetryCountRef.current = 0;
			autoAdvanceArmedRef.current = false;
			const preservesPendingReply =
				active.tutorialId === "quick-start" && active.stepId === "chatSend" && relevantStepId === "firstResponse";
			if (!preservesPendingReply) {
				replyCountBaselineRef.current = null;
			}
			navigateToStep(active.tutorialId, relevantStepId);
			writeTutorialProgress(getTutorialDefinition(active.tutorialId).persistenceKey, relevantStepId);
			setActive({ ...active, stepId: relevantStepId });
			progress.refreshProgress();
		},
		[active, modelItems, navigateToStep, progress.refreshProgress, selectedModelName],
	);

	useEffect(() => {
		if (progress.isSuccess && progress.isQuickStartUnseen && !welcomeHandledRef.current && active === null) {
			setWelcomeOpen(true);
		}
	}, [active, progress.isSuccess, progress.isQuickStartUnseen]);

	const definition = active ? getTutorialDefinition(active.tutorialId) : null;
	const steps = useMemo(
		() => (definition && active ? buildTutorialSteps(t, definition, active.stepIds, TARGET_WAIT_TIMEOUT_MS) : []),
		[active, definition, t],
	);
	const stepIndex = active ? active.stepIds.indexOf(active.stepId) : 0;

	useEffect(() => {
		if (active?.tutorialId !== "quick-start" || active.stepId !== "recommendationInstall" || modelItems === undefined) {
			return;
		}
		if (!hasInstalledChatModel(modelItems)) {
			autoAdvanceArmedRef.current = true;
			return;
		}
		if (autoAdvanceArmedRef.current) {
			if (hasChatCapableDefault(modelItems, selectedModelName) && active.stepIds.includes("navChat")) {
				goToStep("navChat");
			} else if (active.stepIds.includes("setDefaultModel")) {
				goToStep("setDefaultModel");
			}
		}
	}, [active, goToStep, modelItems, selectedModelName]);

	useEffect(() => {
		if (
			active?.tutorialId !== "quick-start" ||
			active.stepId !== "setDefaultModel" ||
			modelItems === undefined ||
			!hasInstalledChatModel(modelItems)
		) {
			return;
		}
		if (!hasChatCapableDefault(modelItems, selectedModelName)) {
			autoAdvanceArmedRef.current = true;
			return;
		}
		if (autoAdvanceArmedRef.current && active.stepIds.includes("navChat")) {
			goToStep("navChat");
		}
	}, [active, goToStep, modelItems, selectedModelName]);

	const replyCount = useVisibleAssistantReplyCount(
		queryClient,
		active?.tutorialId === "quick-start" && (active.stepId === "chatSend" || active.stepId === "firstResponse"),
	);
	useEffect(() => {
		if (active?.tutorialId !== "quick-start" || (active.stepId !== "chatSend" && active.stepId !== "firstResponse")) {
			replyCountBaselineRef.current = null;
			return;
		}
		if (replyCountBaselineRef.current === null || replyCount < replyCountBaselineRef.current) {
			replyCountBaselineRef.current = replyCount;
			return;
		}
		if (active.stepId === "firstResponse" && replyCount > replyCountBaselineRef.current) {
			finish("completed");
		}
	}, [active?.stepId, active?.tutorialId, finish, replyCount]);

	const handleEvent = useCallback(
		(data: EventData) => {
			if (!active) {
				return;
			}
			const { action, index, status, type } = data;
			if (type === EVENTS.TOUR_END || status === STATUS.FINISHED || status === STATUS.SKIPPED) {
				finish(status === STATUS.SKIPPED || action === ACTIONS.SKIP ? "skipped" : "completed");
				return;
			}
			if (action === ACTIONS.CLOSE) {
				finish("skipped");
				return;
			}
			if (type === EVENTS.TARGET_NOT_FOUND) {
				if (targetRetryCountRef.current < MAX_TARGET_RETRIES) {
					targetRetryCountRef.current += 1;
					navigateToStep(active.tutorialId, active.stepId);
					// Give a lazy route one more frame to mount, then refresh the controlled step object so Joyride measures
					// the target again. The bounded counter below guarantees a permanently missing target still advances.
					requestAnimationFrame(() => setActive((current) => (current ? { ...current } : current)));
					return;
				}
				if (index >= active.stepIds.length - 1) {
					finish("completed");
				} else {
					const nextStepId = active.stepIds[index + 1];
					if (nextStepId) {
						goToStep(nextStepId);
					}
				}
				return;
			}
			if (type === EVENTS.STEP_AFTER) {
				if (action !== ACTIONS.PREV && index >= active.stepIds.length - 1) {
					finish("completed");
					return;
				}
				const nextIndex = Math.max(0, Math.min(index + (action === ACTIONS.PREV ? -1 : 1), active.stepIds.length - 1));
				const nextStepId = active.stepIds[nextIndex];
				if (nextStepId) {
					goToStep(nextStepId);
				}
			}
		},
		[active, finish, goToStep, navigateToStep],
	);

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

	const contextValue = useMemo(
		() => ({
			isStateResolved: progress.isResolved,
			isStateSuccessful: progress.isSuccess,
			activeTutorialId: active?.tutorialId ?? null,
			tutorials: progress.tutorials,
			start: (tutorialId: TutorialId) => activate(tutorialId, "start"),
			resume: (tutorialId: TutorialId) => activate(tutorialId, "resume"),
			restart: (tutorialId: TutorialId) => activate(tutorialId, "restart"),
			dismiss,
		}),
		[active?.tutorialId, activate, dismiss, progress.isResolved, progress.isSuccess, progress.tutorials],
	);

	const quickStartHasProgress = progress.tutorials["quick-start"].hasProgress;

	return (
		<OnboardingContext.Provider value={contextValue}>
			<Joyride
				steps={steps}
				run={active !== null}
				stepIndex={stepIndex}
				continuous={true}
				onEvent={handleEvent}
				locale={locale}
				options={{ zIndex: TOUR_Z_INDEX, buttons: ["back", "skip", "primary"] }}
			/>
			<WelcomeTourDialog
				opened={welcomeOpen}
				hasProgress={quickStartHasProgress}
				onStart={() => activate("quick-start", quickStartHasProgress ? "resume" : "start")}
				onSkip={() => dismiss("quick-start")}
			/>
			{children}
		</OnboardingContext.Provider>
	);
}
