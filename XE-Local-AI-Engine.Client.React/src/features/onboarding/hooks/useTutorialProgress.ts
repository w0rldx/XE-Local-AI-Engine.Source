import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import { toast } from "@/core/ui/notifications/Toast";
import type { TutorialUiState } from "@/features/onboarding/context/OnboardingContext";
import { getTutorialDefinition, type TutorialId, tutorialRegistry } from "@/features/onboarding/data/TutorialRegistry";
import { clearTutorialProgress, readTutorialProgress, useTutorialState } from "@/features/onboarding/hooks/useTourState";

export interface TutorialProgressState {
	isResolved: boolean;
	isSuccess: boolean;
	/** True when the quick-start tour has never been completed or skipped, i.e. the welcome invitation is owed. */
	isQuickStartUnseen: boolean;
	tutorials: Readonly<Record<TutorialId, TutorialUiState>>;
	/** Clears the resume point and records the terminal status, unless the tutorial was already completed. */
	markTerminal: (tutorialId: TutorialId, status: "completed" | "skipped") => void;
	/** Re-reads the per-tutorial resume points, which live in localStorage and so are invisible to React. */
	refreshProgress: () => void;
}

export function useTutorialProgress(): TutorialProgressState {
	const { t } = useTranslation();
	const tutorialState = useTutorialState();
	// The server round-trip is optimistic here: the list must show "completed" the moment the tour ends, before the
	// saved state is refetched.
	const [localStatusByKey, setLocalStatusByKey] = useState<Record<string, "completed" | "skipped" | undefined>>({});
	const [, setProgressVersion] = useState(0);

	const refreshProgress = useCallback(() => setProgressVersion((value) => value + 1), []);

	const markTerminal = useCallback(
		(tutorialId: TutorialId, status: "completed" | "skipped") => {
			const persistenceKey = getTutorialDefinition(tutorialId).persistenceKey;
			const wasCompleted =
				localStatusByKey[persistenceKey] === "completed" || tutorialState.statusByKey[persistenceKey] === "completed";
			clearTutorialProgress(persistenceKey);
			if (wasCompleted) {
				return;
			}
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
		[localStatusByKey, t, tutorialState],
	);

	const tutorials = Object.fromEntries(
		tutorialRegistry.map((item) => {
			const localStatus = localStatusByKey[item.persistenceKey];
			const persistedStatus = tutorialState.statusByKey[item.persistenceKey];
			return [
				item.id,
				{
					status: localStatus === "completed" || persistedStatus === "completed" ? "completed" : (localStatus ?? persistedStatus),
					hasProgress: readTutorialProgress(item.persistenceKey, item.stepIds) !== null,
					isAvailable: item.isAvailable,
				},
			];
		}),
	) as Readonly<Record<TutorialId, TutorialUiState>>;

	return {
		isResolved: tutorialState.isResolved,
		isSuccess: tutorialState.isSuccess,
		isQuickStartUnseen: tutorialState.statusByKey[getTutorialDefinition("quick-start").persistenceKey] === undefined,
		tutorials,
		markTerminal,
		refreshProgress,
	};
}
