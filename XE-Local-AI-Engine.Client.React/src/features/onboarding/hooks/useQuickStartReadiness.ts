import { useQuery } from "@tanstack/react-query";
import { useCallback } from "react";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse as LocalModelResponse } from "@/core/api/generated/types.gen";
import { hasChatCapableDefault, hasInstalledChatModel } from "@/features/onboarding/data/TourAdvanceSignals";
import {
	getQuickStartStepIds,
	getTutorialDefinition,
	type QuickStartReadiness,
	type TutorialId,
} from "@/core/onboarding/data/TutorialRegistry";

export interface QuickStartReadinessState {
	/** The installed models, or undefined while the catalog is loading or its read failed. */
	modelItems: readonly LocalModelResponse[] | undefined;
	selectedModelName: string | null | undefined;
	/** The steps the quick-start tour should freeze at activation, given what is installed right now. */
	eligibleStepIds: (tutorialId: TutorialId) => readonly string[];
}

/**
 * The model-catalog half of the quick-start tour: which steps are worth showing, and the two signals the tour's
 * auto-advance effects watch. Only the quick-start tour varies with readiness; every other tour keeps its full list.
 */
export function useQuickStartReadiness(isAuthenticated: boolean): QuickStartReadinessState {
	const modelsQuery = useQuery({ ...listLocalModelsOptions(), enabled: isAuthenticated });

	const classifyQuickStartReadiness = useCallback((): QuickStartReadiness => {
		const modelItems = modelsQuery.data?.items;
		// A failed read is "unresolved", never "missing": it says nothing about what is installed.
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

	return {
		modelItems: modelsQuery.data?.items,
		selectedModelName: modelsQuery.data?.selectedModelName,
		eligibleStepIds,
	};
}
