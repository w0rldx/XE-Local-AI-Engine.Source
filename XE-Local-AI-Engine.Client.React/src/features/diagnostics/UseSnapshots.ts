// Lane B: TanStack Query access to the IndexedDB snapshot store (plan §7.4).
//
// Snapshots are read like server state. The store emits a change event on every mutation (including
// out-of-React captures from `captureSnapshot`), so the read hook subscribes and invalidates — no
// manual invalidation is needed at the capture call site.

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";

import type { Snapshot } from "@/core/diagnostics/Diagnostics";
import { importSnapshot } from "@/features/diagnostics/ExportSnapshot";
import {
	clearSnapshots,
	deleteSnapshot,
	listSnapshots,
	saveSnapshot,
	subscribeSnapshots,
} from "@/features/diagnostics/SnapshotStore";

export const SNAPSHOTS_QUERY_KEY = ["diagnostics", "snapshots"] as const;

/** Read all snapshots (newest first), auto-invalidating when the store mutates. */
export function useSnapshots() {
	const queryClient = useQueryClient();

	useEffect(
		() =>
			subscribeSnapshots(() => {
				queryClient.invalidateQueries({ queryKey: SNAPSHOTS_QUERY_KEY }).catch(() => undefined);
			}),
		[queryClient],
	);

	return useQuery({ queryKey: SNAPSHOTS_QUERY_KEY, queryFn: listSnapshots });
}

/** Delete a snapshot by id. */
export function useDeleteSnapshot() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: (id: string) => deleteSnapshot(id),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: SNAPSHOTS_QUERY_KEY }),
	});
}

/** Remove every snapshot. */
export function useClearSnapshots() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: () => clearSnapshots(),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: SNAPSHOTS_QUERY_KEY }),
	});
}

/** Import a snapshot file, persist it, and surface it in the panel. */
export function useImportSnapshot() {
	const queryClient = useQueryClient();
	return useMutation<Snapshot, Error, File>({
		mutationFn: async (file: File) => {
			const snapshot = await importSnapshot(file);
			await saveSnapshot(snapshot);
			return snapshot;
		},
		onSuccess: () => queryClient.invalidateQueries({ queryKey: SNAPSHOTS_QUERY_KEY }),
	});
}
