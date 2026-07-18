import { useEffect, useRef, useState } from "react";

import {
	type DownloadRateEstimate,
	type DownloadSample,
	appendDownloadSample,
	estimateDownloadRate,
} from "@/features/models/models/DownloadRateEstimate";
import type { GgufDownloadStatus } from "@/features/models/queries/useGgufDownload";

/**
 * UX-11: derives client-side speed + ETA for in-flight GGUF downloads. The status pushes carry byte counts but no
 * timestamps, so this hook captures a wall-clock timestamp each time the status map reference changes (i.e. on each
 * SignalR push / hydrate) and keeps a small rolling sample window per model. It returns a map of modelName →
 * {@link DownloadRateEstimate}; entries appear only once ≥2 samples exist, and a stalled transfer yields no ETA.
 *
 * The sample windows live in a ref (mutated in place) so they persist across renders without themselves triggering
 * re-renders; only the derived estimate map is stateful.
 */
export function useDownloadRateEstimates(
	downloadStatuses: ReadonlyMap<string, GgufDownloadStatus>,
): ReadonlyMap<string, DownloadRateEstimate> {
	const samplesRef = useRef<Map<string, DownloadSample[]>>(new Map());
	const [estimates, setEstimates] = useState<ReadonlyMap<string, DownloadRateEstimate>>(() => new Map());

	useEffect(() => {
		const now = Date.now();
		const samples = samplesRef.current;
		const next = new Map<string, DownloadRateEstimate>();
		const activeNames = new Set<string>();

		for (const [modelName, status] of downloadStatuses) {
			// Only actively-running downloads with a known byte count contribute samples; terminal/indeterminate entries
			// drop their window so a later re-download of the same model starts fresh.
			if (status.phase !== "Running" || status.completedBytes == null) {
				samples.delete(modelName);
				continue;
			}

			activeNames.add(modelName);
			const window = appendDownloadSample(samples.get(modelName), {
				completedBytes: status.completedBytes,
				timestampMs: now,
			});
			samples.set(modelName, window);
			next.set(modelName, estimateDownloadRate(window, status.totalBytes));
		}

		// Prune windows for models no longer present in the status map at all.
		for (const key of [...samples.keys()]) {
			if (!activeNames.has(key)) {
				samples.delete(key);
			}
		}

		setEstimates(next);
	}, [downloadStatuses]);

	return estimates;
}
