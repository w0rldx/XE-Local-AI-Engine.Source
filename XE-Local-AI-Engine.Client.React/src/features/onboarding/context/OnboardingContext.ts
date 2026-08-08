import { createContext, useContext } from "react";

// Minimal imperative surface the OnboardingProvider exposes to descendants. Kept deliberately tiny — the only thing a
// consumer (the About dialog's "Restart tutorial" button) needs is to re-run the tour on demand. Run/stepIndex stay
// private to the provider (single consumer — no Zustand store).
export interface OnboardingContextValue {
	// Re-runs the tour from the first step. Does NOT clear persisted state until the re-run finishes/skips.
	start: () => void;
}

export const OnboardingContext = createContext<OnboardingContextValue | null>(null);

// Returns the onboarding controls when rendered under an OnboardingProvider, or null otherwise. Returning null rather
// than throwing keeps the tour purely additive: a surface that offers "Restart tutorial" can hide the action when the
// provider is absent instead of crashing.
export function useOnboarding(): OnboardingContextValue | null {
	return useContext(OnboardingContext);
}
