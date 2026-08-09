import type { QueryClient } from "@tanstack/react-query";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import { ACTIONS, EVENTS, Joyride, STATUS } from "react-joyride";
import type { EventData } from "react-joyride";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { router } from "@/core/integrations/tanstack-router/Router";
import { toast } from "@/core/ui/notifications/Toast";
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
import {
	countVisibleAssistantReplies,
	hasChatCapableDefault,
	hasInstalledChatModel,
} from "@/features/onboarding/data/TourAdvanceSignals";
import {
	clearTutorialProgress,
	readTutorialProgress,
	useTutorialState,
	writeTutorialProgress,
} from "@/features/onboarding/hooks/useTourState";

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
	const tutorialState = useTutorialState();
	const isAuthenticated = useNodeAuthStore((state) => Boolean(state.accessToken));
	const modelsQuery = useQuery(withResponseValidation({ ...listLocalModelsOptions(), enabled: isAuthenticated }));
	const [active, setActive] = useState<ActiveTutorial | null>(null);
	const [welcomeOpen, setWelcomeOpen] = useState(false);
	const [, setProgressVersion] = useState(0);
	const [localStatusByKey, setLocalStatusByKey] = useState<Record<string, "completed" | "skipped" | undefined>>({});
	const welcomeHandledRef = useRef(false);
	const targetRetryCountRef = useRef(0);
	const autoAdvanceArmedRef = useRef(false);
	const replyCountBaselineRef = useRef<number | null>(null);

	const classifyQuickStartReadiness = useCallback((): QuickStartReadiness => {
		const modelItems = modelsQuery.data?.items;
		if (!modelsQuery.isSuccess || modelsQuery.data?.isAvailable !== true || modelItems === undefined) {
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
			setProgressVersion((value) => value + 1);
			setActive({ tutorialId, stepIds: frozenStepIds, stepId });
		},
		[active, eligibleStepIds, navigateToStep],
	);

	const persistTerminalStatus = useCallback(
		(persistenceKey: string, status: "completed" | "skipped") => {
			tutorialState.markDone(persistenceKey, status, {
				onSuccess: () => {
					setLocalStatusByKey((current) => ({
						...current,
						[persistenceKey]: current[persistenceKey] === "completed" ? "completed" : status,
					}));
				},
				onError: () => toast.error(t("onboarding.errors.saveState")),
			});
		},
		[t, tutorialState],
	);

	const finish = useCallback(
		(status: "completed" | "skipped") => {
			if (!active) {
				return;
			}
			const persistenceKey = getTutorialDefinition(active.tutorialId).persistenceKey;
			const wasCompleted =
				localStatusByKey[persistenceKey] === "completed" || tutorialState.statusByKey[persistenceKey] === "completed";
			clearTutorialProgress(persistenceKey);
			if (!wasCompleted) {
				persistTerminalStatus(persistenceKey, status);
			}
			autoAdvanceArmedRef.current = false;
			replyCountBaselineRef.current = null;
			setActive(null);
			setProgressVersion((value) => value + 1);
		},
		[active, localStatusByKey, persistTerminalStatus, tutorialState.statusByKey],
	);

	const dismiss = useCallback(
		(tutorialId: TutorialId) => {
			const definition = getTutorialDefinition(tutorialId);
			const wasCompleted =
				localStatusByKey[definition.persistenceKey] === "completed" ||
				tutorialState.statusByKey[definition.persistenceKey] === "completed";
			clearTutorialProgress(definition.persistenceKey);
			if (!wasCompleted) {
				persistTerminalStatus(definition.persistenceKey, "skipped");
			}
			if (tutorialId === "quick-start") {
				welcomeHandledRef.current = true;
				setWelcomeOpen(false);
			}
			setProgressVersion((value) => value + 1);
		},
		[localStatusByKey, persistTerminalStatus, tutorialState.statusByKey],
	);

	const goToStep = useCallback(
		(stepId: string) => {
			if (!active) {
				return;
			}
			let relevantStepId = stepId;
			const liveModelItems = modelsQuery.data?.isAvailable === true ? modelsQuery.data.items : undefined;
			if (active.tutorialId === "quick-start" && liveModelItems !== undefined) {
				const hasInstalledModel = hasInstalledChatModel(liveModelItems);
				const hasDefaultModel = hasChatCapableDefault(liveModelItems, modelsQuery.data?.selectedModelName);
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
			replyCountBaselineRef.current = null;
			navigateToStep(active.tutorialId, relevantStepId);
			writeTutorialProgress(getTutorialDefinition(active.tutorialId).persistenceKey, relevantStepId);
			setActive({ ...active, stepId: relevantStepId });
			setProgressVersion((value) => value + 1);
		},
		[active, modelsQuery.data, navigateToStep],
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
	const modelItems = modelsQuery.data?.isAvailable === true ? modelsQuery.data.items : undefined;
	const selectedModelName = modelsQuery.data?.selectedModelName;

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
		active?.tutorialId === "quick-start" && active.stepId === "firstResponse",
	);
	useEffect(() => {
		if (active?.tutorialId !== "quick-start" || active.stepId !== "firstResponse") {
			replyCountBaselineRef.current = null;
			return;
		}
		if (replyCountBaselineRef.current === null || replyCount < replyCountBaselineRef.current) {
			replyCountBaselineRef.current = replyCount;
			return;
		}
		if (replyCount > replyCountBaselineRef.current) {
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

	const tutorialUiState = Object.fromEntries(
		tutorialRegistry.map((item) => {
			const localStatus = localStatusByKey[item.persistenceKey];
			const persistedStatus = tutorialState.statusByKey[item.persistenceKey];
			return [
				item.id,
				{
					status:
						localStatus === "completed" || persistedStatus === "completed"
							? "completed"
							: (localStatus ?? persistedStatus),
					hasProgress: readTutorialProgress(item.persistenceKey, item.stepIds) !== null,
					isAvailable: item.isAvailable,
				},
			];
		}),
	) as Readonly<Record<TutorialId, TutorialUiState>>;

	const contextValue = useMemo(
		() => ({
			isStateResolved: tutorialState.isResolved,
			isStateSuccessful: tutorialState.isSuccess,
			activeTutorialId: active?.tutorialId ?? null,
			tutorials: tutorialUiState,
			start: (tutorialId: TutorialId) => activate(tutorialId, "start"),
			resume: (tutorialId: TutorialId) => activate(tutorialId, "resume"),
			restart: (tutorialId: TutorialId) => activate(tutorialId, "restart"),
			dismiss,
		}),
		[active?.tutorialId, activate, dismiss, tutorialState.isResolved, tutorialState.isSuccess, tutorialUiState],
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

function useVisibleAssistantReplyCount(queryClient: QueryClient, active: boolean): number {
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
		() => (active ? countVisibleAssistantReplies(queryClient) : 0),
		[active, queryClient],
	);

	return useSyncExternalStore(subscribe, getSnapshot);
}
