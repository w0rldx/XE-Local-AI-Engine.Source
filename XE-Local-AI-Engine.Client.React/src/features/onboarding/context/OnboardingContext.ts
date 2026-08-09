import { createContext, useContext } from "react";

import type { TutorialId } from "@/features/onboarding/data/TutorialRegistry";
import type { TutorialStatus } from "@/features/onboarding/hooks/useTourState";

export interface TutorialUiState {
	status?: TutorialStatus;
	hasProgress: boolean;
	isAvailable: boolean;
}

export interface OnboardingContextValue {
	isStateResolved: boolean;
	isStateSuccessful: boolean;
	tutorials: Readonly<Record<TutorialId, TutorialUiState>>;
	start: (tutorialId: TutorialId) => void;
	resume: (tutorialId: TutorialId) => void;
	restart: (tutorialId: TutorialId) => void;
	dismiss: (tutorialId: TutorialId) => void;
}

export const OnboardingContext = createContext<OnboardingContextValue | null>(null);

export function useOnboarding(): OnboardingContextValue | null {
	return useContext(OnboardingContext);
}
