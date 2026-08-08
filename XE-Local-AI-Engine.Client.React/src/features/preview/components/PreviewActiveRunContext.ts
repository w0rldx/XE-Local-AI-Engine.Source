import { createContext, useContext } from "react";

// The runId whose live output the canvas nodes should display. A single Preview tab can have several runs in
// flight (the store tracks each), but the canvas shows ONE at a time — the most recently started run — so the
// node components read this context to pick the right per-run state out of PreviewRunStore. null when no run is
// active (the store is empty / nothing executed yet).
export const PreviewActiveRunContext = createContext<string | null>(null);

export function useActiveRunId(): string | null {
	return useContext(PreviewActiveRunContext);
}
