import type { TutorialId } from "@/features/onboarding/data/TutorialRegistry";

/** The tutorial the provider is currently running, and how far into it the operator is. */
export interface ActiveTutorial {
	tutorialId: TutorialId;
	stepIds: readonly string[];
	stepId: string;
}
