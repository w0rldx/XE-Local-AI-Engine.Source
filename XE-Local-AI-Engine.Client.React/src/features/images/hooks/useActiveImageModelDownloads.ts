import { useCallback, useMemo, useState } from "react";

import type { ImageModelDownloadView } from "@/features/images/models/ImageModels";
import { useImageModelDownloads } from "@/features/images/queries/useImageQueries";

/** What {@link useActiveImageModelDownloads} hands back to the model manager. */
export interface ActiveImageModelDownloads {
	/** Tracked downloads keyed by model name — the shape `useDownloadRateEstimates` samples for speed + ETA. */
	readonly statuses: ReadonlyMap<string, ImageModelDownloadView>;
	/** Tracked model names still transferring, in the order they were started. */
	readonly inFlight: readonly string[];
	/** Starts tracking a model name (call once the start mutation is accepted). */
	readonly track: (modelName: string) => void;
	/** Stops tracking a model name, once its terminal phase has been handled. */
	readonly untrack: (modelName: string) => void;
}

/**
 * Tracks EVERY in-flight image-model download rather than a single pending slot.
 *
 * The manager used to hold one `pendingModelName`, which meant starting one download disabled the Install button on
 * every other model — fine while the form could only describe one hand-typed model, wrong the moment a catalog offers
 * several. This mirrors the GGUF lane's `useActiveGgufDownloads`: a set of tracked names reconciled against the
 * coordinator's status list.
 *
 * It polls instead of subscribing, because the image lane has no download hub yet (the GGUF one pushes over SignalR).
 * The poll is gated on there being something to watch, so an idle page issues no requests.
 */
export function useActiveImageModelDownloads(): ActiveImageModelDownloads {
	const [tracked, setTracked] = useState<readonly string[]>([]);
	const downloadsQuery = useImageModelDownloads(tracked.length > 0);

	const statuses = useMemo(() => {
		const map = new Map<string, ImageModelDownloadView>();
		for (const entry of downloadsQuery.data ?? []) {
			if (tracked.includes(entry.modelName)) {
				map.set(entry.modelName, entry);
			}
		}
		return map;
	}, [downloadsQuery.data, tracked]);

	// A tracked name the coordinator has not reported yet counts as running: the start was accepted, so showing nothing
	// until the first poll lands would flash the row out of existence for up to a poll interval.
	const inFlight = useMemo(
		() => tracked.filter((modelName) => (statuses.get(modelName)?.phase ?? "Running") === "Running"),
		[tracked, statuses],
	);

	const track = useCallback((modelName: string) => {
		setTracked((current) => (current.includes(modelName) ? current : [...current, modelName]));
	}, []);

	const untrack = useCallback((modelName: string) => {
		setTracked((current) => (current.includes(modelName) ? current.filter((name) => name !== modelName) : current));
	}, []);

	return { statuses, inFlight, track, untrack };
}
