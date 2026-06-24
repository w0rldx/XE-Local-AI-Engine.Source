import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";

import {
	getTutorialStateOptions,
	getTutorialStateQueryKey,
	saveTutorialStateMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

// The single tour key shipped today. The persistence shape is an array keyed by tour key so a future second tour can
// reuse the same column/endpoint without a migration, but only this one is built (plan §1 non-goals).
export const MAIN_APP_TOUR_KEY = "main-app-v1";

// A recorded tour outcome suppresses the welcome prompt regardless of which terminal status it carries.
export type TourStatus = "completed" | "skipped";

export interface UseTourStateResult {
	// True only when the GET succeeded and carries no recorded entry for this tour key (neither completed nor skipped).
	// While the query is loading or errored we do NOT prompt — the tour is purely additive and must never gate the app
	// on a failed read (plan §3 architecture invariant).
	shouldPrompt: boolean;
	// True once the GET has resolved (success or error) so the provider can decide whether to surface the welcome dialog
	// without flashing it before the persisted state is known.
	isResolved: boolean;
	// Persists a terminal outcome for this tour key (upsert) and invalidates the GET so a reload reflects it.
	markDone: (status: TourStatus) => void;
}

// Reads the authenticated user's recorded tour entries through the generated hey-api hooks (never a hand-rolled fetch,
// plan §4 reuse) and derives whether the welcome dialog should be offered. The mutation upserts one entry by key; the
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
