import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";

import {
	getTutorialStateOptions,
	getTutorialStateQueryKey,
	saveTutorialStateMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

// The single tour key shipped today. The persistence shape is an array keyed by tour key so a future second tour can
// reuse the same column/endpoint without a migration, but only this one is built.
export const MAIN_APP_TOUR_KEY = "main-app-v1";

// Namespaced localStorage key holding the in-progress step index so a reload mid-tour resumes instead of restarting
// (Bug B). Client-only and best-effort: the backend still records the TERMINAL status; this only carries transient
// progress and is always cleared on finish(). Follows the `xe-` key convention used by the other client stores.
export const TOUR_PROGRESS_STORAGE_KEY = `xe-onboarding-${MAIN_APP_TOUR_KEY}-step`;

// Reads the persisted in-progress step index, or null when none is stored / storage is unavailable / the value is
// not a non-negative integer. Range-validity against the live step count is the caller's responsibility (the step
// array length is owned by the provider, not this hook).
export function readTourProgress(): number | null {
	try {
		const raw = globalThis.localStorage?.getItem(TOUR_PROGRESS_STORAGE_KEY);
		if (raw === null || raw === undefined) {
			return null;
		}
		const parsed = Number.parseInt(raw, 10);
		return Number.isInteger(parsed) && parsed >= 0 ? parsed : null;
	} catch {
		return null;
	}
}

// Persists the in-progress step index. Best-effort: storage failures (quota / unavailable) are swallowed so the tour
// still runs in-memory.
export function writeTourProgress(index: number): void {
	try {
		globalThis.localStorage?.setItem(TOUR_PROGRESS_STORAGE_KEY, String(index));
	} catch {
		// Ignore unavailable storage or quota errors; the tour continues from in-memory state.
	}
}

// Clears the in-progress step index. Called on every finish() (completed or skipped) so a terminated tour can never
// resurrect on the next reload.
export function clearTourProgress(): void {
	try {
		globalThis.localStorage?.removeItem(TOUR_PROGRESS_STORAGE_KEY);
	} catch {
		// Ignore unavailable storage errors.
	}
}

// A recorded tour outcome suppresses the welcome prompt regardless of which terminal status it carries.
export type TourStatus = "completed" | "skipped";

export interface UseTourStateResult {
	// True only when the GET succeeded and carries no recorded entry for this tour key (neither completed nor skipped).
	// While the query is loading or errored we do NOT prompt — the tour is purely additive and must never gate the app
	// on a failed read.
	shouldPrompt: boolean;
	// True once the GET has resolved (success or error) so the provider can decide whether to surface the welcome dialog
	// without flashing it before the persisted state is known.
	isResolved: boolean;
	// Persists a terminal outcome for this tour key (upsert) and invalidates the GET so a reload reflects it.
	markDone: (status: TourStatus) => void;
}

// Reads the authenticated user's recorded tour entries through the generated hey-api hooks (never a hand-rolled fetch)
// and derives whether the welcome dialog should be offered. The mutation upserts one entry by key; the
// backend merges it into the JSON array so other tour keys are preserved.
export function useTourState(tourKey: string = MAIN_APP_TOUR_KEY): UseTourStateResult {
	const queryClient = useQueryClient();
	// The provider is mounted globally (outside the auth-gated routes), so without this gate the GET fires pre-login
	// with an empty bearer → 401 → the query sticks in `error` and the welcome dialog never surfaces even after login.
	// Enabling only once an access token exists makes it fire post-login with a valid bearer.
	const isAuthenticated = useNodeAuthStore((state) => Boolean(state.accessToken));
	const stateQuery = useQuery({ ...getTutorialStateOptions(), enabled: isAuthenticated });
	const saveMutation = useMutation(saveTutorialStateMutation());

	const entries = stateQuery.data?.entries ?? [];
	const hasRecordedEntry = entries.some((entry) => entry.key === tourKey && (entry.status === "completed" || entry.status === "skipped"));

	const isResolved = stateQuery.isSuccess || stateQuery.isError;
	const shouldPrompt = stateQuery.isSuccess && !hasRecordedEntry;

	const markDone = useCallback(
		(status: TourStatus) => {
			saveMutation.mutate(
				{ body: { key: tourKey, status } },
				{
					onSuccess: () => {
						queryClient.invalidateQueries({ queryKey: getTutorialStateQueryKey() }).catch(() => undefined);
					},
				},
			);
		},
		[queryClient, saveMutation, tourKey],
	);

	return { shouldPrompt, isResolved, markDone };
}
