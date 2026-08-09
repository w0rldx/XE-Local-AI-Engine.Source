import { useQuery } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { ACTIONS, EVENTS, Joyride, STATUS } from "react-joyride";
import type { EventData } from "react-joyride";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { router } from "@/core/integrations/tanstack-router/Router";
import { WelcomeTourDialog } from "@/features/onboarding/components/WelcomeTourDialog";
import { OnboardingContext, type TutorialUiState } from "@/features/onboarding/context/OnboardingContext";
import {
	buildTutorialSteps,
	getQuickStartStepIds,
	getTutorialDefinition,
	resolveResumeStepId,
	tutorialRegistry,
	type QuickStartReadiness,
	type TutorialId,
} from "@/features/onboarding/data/TutorialRegistry";
import { hasChatCapableDefault, hasInstalledChatModel } from "@/features/onboarding/data/TourAdvanceSignals";
import {
	clearTutorialProgress,
	readTutorialProgress,
	useTutorialState,
	writeTutorialProgress,
} from "@/features/onboarding/hooks/useTourState";

const TOUR_Z_INDEX = 1000;
const TARGET_WAIT_TIMEOUT_MS = 3000;
const MAX_TARGET_RETRIES = 4;

/* eslint-disable react-doctor/no-adjust-state-on-prop-change -- the optional welcome invitation opens only after the authenticated backend state resolves successfully. */

interface ActiveTutorial {
	tutorialId: TutorialId;
	stepIds: readonly string[];
	stepId: string;
}

export function OnboardingProvider({ children }: { children: ReactNode }) {
	const { t } = useTranslation();
	const tutorialState = useTutorialState();
	const isAuthenticated = useNodeAuthStore((state) => Boolean(state.accessToken));
	const modelsQuery = useQuery(withResponseValidation({ ...listLocalModelsOptions(), enabled: isAuthenticated }));
	const [active, setActive] = useState<ActiveTutorial | null>(null);
	const [welcomeOpen, setWelcomeOpen] = useState(false);
	const [, setProgressVersion] = useState(0);
	const [localStatusByKey, setLocalStatusByKey] = useState<Record<string, "completed" | "skipped" | undefined>>({});
	const welcomeHandledRef = useRef(false);
	const targetRetryCountRef = useRef(0);

	const classifyQuickStartReadiness = useCallback((): QuickStartReadiness => {
		const modelItems = modelsQuery.data?.items;
		if (!modelsQuery.isSuccess || modelItems === undefined) {
			return "unresolved";
		}
		if (hasChatCapableDefault(modelItems, modelsQuery.data?.selectedModelName)) {
			return "ready";
		}
		return hasInstalledChatModel(modelItems) ? "installed-unselected" : "missing";
	}, [modelsQuery.data, modelsQuery.isSuccess]);

	const eligibleStepIds = useCallback(
		(tutorialId: TutorialId): readonly string[] =>
			tutorialId === "quick-start"
				? getQuickStartStepIds(classifyQuickStartReadiness())
				: getTutorialDefinition(tutorialId).stepIds,
		[classifyQuickStartReadiness],
	);

	const navigateToStep = useCallback((tutorialId: TutorialId, stepId: string) => {
		const route = getTutorialDefinition(tutorialId).routeByStepId[stepId];
		if (route && router.state.location.pathname !== route) {
			router.navigate({ to: route }).catch(() => undefined);
		}
	}, []);

	const activate = useCallback(
		(tutorialId: TutorialId, mode: "start" | "resume" | "restart") => {
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
			navigateToStep(tutorialId, stepId);
			writeTutorialProgress(definition.persistenceKey, stepId);
			setProgressVersion((value) => value + 1);
			setActive({ tutorialId, stepIds: frozenStepIds, stepId });
		},
		[eligibleStepIds, navigateToStep],
	);

	const finish = useCallback(
		(status: "completed" | "skipped") => {
			if (!active) {
				return;
			}
			const persistenceKey = getTutorialDefinition(active.tutorialId).persistenceKey;
			clearTutorialProgress(persistenceKey);
			tutorialState.markDone(persistenceKey, status);
			setLocalStatusByKey((current) => ({
				...current,
				[persistenceKey]:
					current[persistenceKey] === "completed" || tutorialState.statusByKey[persistenceKey] === "completed"
						? "completed"
						: status,
			}));
			setActive(null);
			setProgressVersion((value) => value + 1);
		},
		[active, tutorialState],
	);

	const dismiss = useCallback(
		(tutorialId: TutorialId) => {
			const definition = getTutorialDefinition(tutorialId);
			clearTutorialProgress(definition.persistenceKey);
			tutorialState.markDone(definition.persistenceKey, "skipped");
			setLocalStatusByKey((current) => ({
				...current,
				[definition.persistenceKey]:
					tutorialState.statusByKey[definition.persistenceKey] === "completed" ? "completed" : "skipped",
			}));
			if (tutorialId === "quick-start") {
				welcomeHandledRef.current = true;
				setWelcomeOpen(false);
			}
			setProgressVersion((value) => value + 1);
		},
		[tutorialState],
	);

	const goToStep = useCallback(
		(stepId: string) => {
			if (!active) {
				return;
			}
			targetRetryCountRef.current = 0;
			navigateToStep(active.tutorialId, stepId);
			writeTutorialProgress(getTutorialDefinition(active.tutorialId).persistenceKey, stepId);
			setActive({ ...active, stepId });
			setProgressVersion((value) => value + 1);
		},
		[active, navigateToStep],
	);

	useEffect(() => {
		const quickStartStatus = tutorialState.statusByKey[getTutorialDefinition("quick-start").persistenceKey];
		if (tutorialState.isSuccess && quickStartStatus === undefined && !welcomeHandledRef.current && active === null) {
			setWelcomeOpen(true);
		}
	}, [active, tutorialState.isSuccess, tutorialState.statusByKey]);

	const definition = active ? getTutorialDefinition(active.tutorialId) : null;
	const steps = useMemo(
		() => (definition && active ? buildTutorialSteps(t, definition, active.stepIds, TARGET_WAIT_TIMEOUT_MS) : []),
		[active, definition, t],
	);
	const stepIndex = active ? active.stepIds.indexOf(active.stepId) : 0;

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

	const tutorialUiState = Object.fromEntries(
		tutorialRegistry.map((item) => [
			item.id,
			{
				status: localStatusByKey[item.persistenceKey] ?? tutorialState.statusByKey[item.persistenceKey],
				hasProgress: readTutorialProgress(item.persistenceKey, item.stepIds) !== null,
				isAvailable: item.isAvailable,
			},
		]),
	) as Readonly<Record<TutorialId, TutorialUiState>>;

	const contextValue = useMemo(
		() => ({
			isStateResolved: tutorialState.isResolved,
			isStateSuccessful: tutorialState.isSuccess,
			tutorials: tutorialUiState,
			start: (tutorialId: TutorialId) => activate(tutorialId, "start"),
			resume: (tutorialId: TutorialId) => activate(tutorialId, "resume"),
			restart: (tutorialId: TutorialId) => activate(tutorialId, "restart"),
			dismiss,
		}),
		[activate, dismiss, tutorialState.isResolved, tutorialState.isSuccess, tutorialUiState],
	);

	const quickStartHasProgress = tutorialUiState["quick-start"].hasProgress;

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
