import { useEffect, useRef, useState } from "react";

import {
	type DownloadRateEstimate,
	type DownloadSample,
	appendDownloadSample,
	estimateDownloadRate,
} from "@/features/models/models/DownloadRateEstimate";
/**
 * The minimum a tracked transfer must expose to be rate-estimated. Structural (not tied to `GgufDownloadStatus`) so the
 * same windowing serves any byte-progress channel — the GGUF download hub and the llama.cpp runtime acquisition hub
 * both satisfy it.
 */
export interface RateTrackedProgress {
	readonly phase: string;
	readonly completedBytes: number | null | undefined;
	readonly totalBytes: number | null | undefined;
}

/** The phase name the GGUF download channel reports while bytes are moving. */
const DEFAULT_ACTIVE_PHASE = "Running";

/**
 * UX-11: derives client-side speed + ETA for in-flight byte transfers. The status pushes carry byte counts but no
 * timestamps, so this hook captures a wall-clock timestamp each time the status map reference changes (i.e. on each
 * SignalR push / hydrate) and keeps a small rolling sample window per key. It returns a map of key →
 * {@link DownloadRateEstimate}; entries appear only once ≥2 samples exist, and a stalled transfer yields no ETA.
 *
 * `activePhase` names the one phase during which bytes actually move; samples are taken only then. It is parameterized
 * rather than hard-coded because the channels disagree: GGUF downloads report `Running`, while runtime acquisition
 * reports `Downloading` among several non-terminal phases (Verifying/Extracting move no bytes). Hard-coding `Running`
 * would silently drop every sample from the runtime banner, leaving it with no rate and no ETA — the single most useful
 * thing to show on the slow connection this exists for.
 *
 * The sample windows live in a ref (mutated in place) so they persist across renders without themselves triggering
 * re-renders; only the derived estimate map is stateful.
 */
export function useDownloadRateEstimates(
	downloadStatuses: ReadonlyMap<string, RateTrackedProgress>,
	activePhase: string = DEFAULT_ACTIVE_PHASE,
): ReadonlyMap<string, DownloadRateEstimate> {
	const samplesRef = useRef<Map<string, DownloadSample[]>>(new Map());
	const [estimates, setEstimates] = useState<ReadonlyMap<string, DownloadRateEstimate>>(() => new Map());

	useEffect(() => {
		const now = Date.now();
		const samples = samplesRef.current;
		const next = new Map<string, DownloadRateEstimate>();
		const activeNames = new Set<string>();

		for (const [key, status] of downloadStatuses) {
			// Only transfers in the active phase with a known byte count contribute samples; terminal/indeterminate
			// entries drop their window so a later retry of the same key starts fresh.
			if (status.phase !== activePhase || status.completedBytes == null) {
				samples.delete(key);
				continue;
			}

			activeNames.add(key);
			const window = appendDownloadSample(samples.get(key), {
				completedBytes: status.completedBytes,
				timestampMs: now,
			});
			samples.set(key, window);
			next.set(key, estimateDownloadRate(window, status.totalBytes));
		}

		// Prune windows for keys no longer present in the status map at all.
		for (const key of [...samples.keys()]) {
			if (!activeNames.has(key)) {
				samples.delete(key);
			}
		}

		setEstimates(next);
	}, [downloadStatuses, activePhase]);

	return estimates;
}
