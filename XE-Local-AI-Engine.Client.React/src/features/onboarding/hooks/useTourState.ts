import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useMemo } from "react";

import {
	getTutorialStateOptions,
	getTutorialStateQueryKey,
	saveTutorialStateMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

export type TutorialStatus = "completed" | "skipped";

export interface TutorialProgress {
	format: 1;
	stepId: string;
}

export function tutorialProgressStorageKey(persistenceKey: string): string {
	// Keep the original key so legacy numeric values are encountered and actively cleared instead of becoming orphaned.
	return `xe-onboarding-${persistenceKey}-step`;
}

export function readTutorialProgress(persistenceKey: string, knownStepIds: readonly string[]): TutorialProgress | null {
	const storageKey = tutorialProgressStorageKey(persistenceKey);
	try {
		const raw = globalThis.localStorage?.getItem(storageKey);
		if (raw === null || raw === undefined) {
			return null;
		}
		const parsed = JSON.parse(raw) as Partial<TutorialProgress>;
		if (parsed.format !== 1 || typeof parsed.stepId !== "string" || !knownStepIds.includes(parsed.stepId)) {
			globalThis.localStorage?.removeItem(storageKey);
			return null;
		}
		return { format: 1, stepId: parsed.stepId };
	} catch {
		try {
			globalThis.localStorage?.removeItem(storageKey);
		} catch {
			// Storage is best-effort; an unavailable store must not prevent a tutorial from running.
		}
		return null;
	}
}

export function writeTutorialProgress(persistenceKey: string, stepId: string): void {
	try {
		globalThis.localStorage?.setItem(tutorialProgressStorageKey(persistenceKey), JSON.stringify({ format: 1, stepId }));
	} catch {
		// Storage is best-effort; the active tutorial continues in memory.
	}
}

export function clearTutorialProgress(persistenceKey: string): void {
	try {
		globalThis.localStorage?.removeItem(tutorialProgressStorageKey(persistenceKey));
	} catch {
		// Storage is best-effort.
	}
}

export interface UseTutorialStateResult {
	isResolved: boolean;
	isSuccess: boolean;
	statusByKey: Readonly<Record<string, TutorialStatus | undefined>>;
	markDone: (
		persistenceKey: string,
		status: TutorialStatus,
		callbacks?: { onSuccess?: () => void; onError?: () => void },
	) => void;
}

export function useTutorialState(): UseTutorialStateResult {
	const queryClient = useQueryClient();
	const isAuthenticated = useNodeAuthStore((state) => Boolean(state.accessToken));
	const stateQuery = useQuery(withResponseValidation({ ...getTutorialStateOptions(), enabled: isAuthenticated }));
	const saveMutation = useMutation({
		...withResponseValidation(saveTutorialStateMutation()),
		scope: { id: "tutorial-state" },
	});

	const statusByKey = useMemo(() => {
		const result: Record<string, TutorialStatus | undefined> = {};
		for (const entry of stateQuery.data?.entries ?? []) {
			if (entry.status === "completed" || entry.status === "skipped") {
				result[entry.key] = entry.status;
			}
		}
		return result;
	}, [stateQuery.data?.entries]);

	const markDone = useCallback(
		(persistenceKey: string, status: TutorialStatus, callbacks?: { onSuccess?: () => void; onError?: () => void }) => {
			// Completion is monotonic. A user may replay and close a completed tutorial without losing that achievement.
			if (status === "skipped" && statusByKey[persistenceKey] === "completed") {
				return;
			}
			saveMutation
				.mutateAsync({ body: { key: persistenceKey, status } })
				.then(() => {
					queryClient.invalidateQueries({ queryKey: getTutorialStateQueryKey() }).catch(() => undefined);
					callbacks?.onSuccess?.();
				})
				.catch(() => callbacks?.onError?.());
		},
		[queryClient, saveMutation, statusByKey],
	);

	return {
		isResolved: stateQuery.isSuccess || stateQuery.isError,
		isSuccess: stateQuery.isSuccess,
		statusByKey,
		markDone,
	};
}
