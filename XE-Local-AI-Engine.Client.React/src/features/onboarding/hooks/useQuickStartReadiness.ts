import { useQuery } from "@tanstack/react-query";
import { useCallback } from "react";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse as LocalModelResponse } from "@/core/api/generated/types.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { hasChatCapableDefault, hasInstalledChatModel } from "@/features/onboarding/data/TourAdvanceSignals";
import {
	getQuickStartStepIds,
	getTutorialDefinition,
	type QuickStartReadiness,
	type TutorialId,
} from "@/features/onboarding/data/TutorialRegistry";

export interface QuickStartReadinessState {
	/** The installed models, or undefined while the runtime has not reported an available catalogue yet. */
	modelItems: readonly LocalModelResponse[] | undefined;
	selectedModelName: string | null | undefined;
	/** The steps the quick-start tour should freeze at activation, given what is installed right now. */
	eligibleStepIds: (tutorialId: TutorialId) => readonly string[];
}

/**
 * The model-catalogue half of the quick-start tour: which steps are worth showing, and the two signals the tour's
 * auto-advance effects watch. Only the quick-start tour varies with readiness; every other tour keeps its full list.
 */
export function useQuickStartReadiness(isAuthenticated: boolean): QuickStartReadinessState {
	const modelsQuery = useQuery(withResponseValidation({ ...listLocalModelsOptions(), enabled: isAuthenticated }));

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

	return {
		modelItems: modelsQuery.data?.isAvailable === true ? modelsQuery.data.items : undefined,
		selectedModelName: modelsQuery.data?.selectedModelName,
		eligibleStepIds,
	};
}
