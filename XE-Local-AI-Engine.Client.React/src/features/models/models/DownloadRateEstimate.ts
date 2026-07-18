// UX-11: client-side download speed + ETA. The GGUF download status pushes (useGgufDownload) carry byte counts but NO
// timestamps, so speed cannot be read off the wire. Instead the panel captures a wall-clock timestamp on each push and
// keeps a small rolling window of samples per model; these pure helpers derive speed/ETA from that window so the math
// is unit-testable in isolation from React.

/** One observed byte-progress reading for an in-flight download, timestamped when the panel received the push. */
export interface DownloadSample {
	readonly completedBytes: number;
	readonly timestampMs: number;
}

/** Derived, smoothed rate estimate. Fields are undefined when there is not enough signal to state them honestly. */
export interface DownloadRateEstimate {
	/** Smoothed bytes/second across the sample window, or undefined with fewer than two samples. */
	readonly bytesPerSecond: number | undefined;
	/** Seconds remaining, or undefined when speed is unknown/zero (stalled) or the total size is unknown. */
	readonly etaSeconds: number | undefined;
}

// The most samples kept per model. A short window smooths the jitter between individual SignalR pushes without lagging
// far behind a changing transfer rate.
export const DOWNLOAD_SAMPLE_WINDOW = 6;

const EMPTY_ESTIMATE: DownloadRateEstimate = { bytesPerSecond: undefined, etaSeconds: undefined };

/**
 * Appends a reading to a per-model sample window, returning a NEW capped array (the newest sample last). A reading whose
 * byte count/timestamp did not advance past the previous sample is ignored so duplicate/no-op pushes never distort the
 * window or manufacture a fake "0 elapsed" delta.
 */
export function appendDownloadSample(
	previous: readonly DownloadSample[] | undefined,
	sample: DownloadSample,
): DownloadSample[] {
	const window = previous ? [...previous] : [];
	const last = window.at(-1);
	if (last && sample.timestampMs <= last.timestampMs && sample.completedBytes <= last.completedBytes) {
		return window;
	}
	window.push(sample);
	if (window.length > DOWNLOAD_SAMPLE_WINDOW) {
		window.splice(0, window.length - DOWNLOAD_SAMPLE_WINDOW);
	}
	return window;
}

/**
 * Estimates transfer speed + ETA from a sample window. Speed is the total bytes moved across the window divided by its
 * wall-clock span (a moving average, not the instantaneous last-delta). ETA needs a known total AND a positive speed;
 * a stalled transfer (no bytes gained across the window) yields a 0 speed and NO ETA rather than a misleading "∞".
 */
export function estimateDownloadRate(
	samples: readonly DownloadSample[],
	totalBytes: number | null | undefined,
): DownloadRateEstimate {
	const first = samples.at(0);
	const last = samples.at(-1);
	if (!first || !last || samples.length < 2) {
		return EMPTY_ESTIMATE;
	}

	const elapsedMs = last.timestampMs - first.timestampMs;
	const deltaBytes = last.completedBytes - first.completedBytes;

	if (elapsedMs <= 0) {
		return EMPTY_ESTIMATE;
	}

	const bytesPerSecond = Math.max(0, (deltaBytes / elapsedMs) * 1000);

	if (bytesPerSecond <= 0 || totalBytes == null || totalBytes <= 0) {
		return { bytesPerSecond, etaSeconds: undefined };
	}

	const remainingBytes = Math.max(0, totalBytes - last.completedBytes);
	return { bytesPerSecond, etaSeconds: remainingBytes / bytesPerSecond };
}

/**
 * Formats a remaining-seconds estimate as a compact, locale-neutral duration (e.g. "45s", "2m 10s", "1h 5m"). The
 * caller wraps this in a localized "~{{duration}} left" label. Returns undefined for a missing/non-finite estimate so
 * the caller can render a neutral placeholder instead.
 */
export function formatDownloadEta(etaSeconds: number | undefined): string | undefined {
	if (etaSeconds == null || !Number.isFinite(etaSeconds) || etaSeconds < 0) {
		return undefined;
	}

	const totalSeconds = Math.round(etaSeconds);
	if (totalSeconds < 60) {
		return `${totalSeconds}s`;
	}

	const hours = Math.floor(totalSeconds / 3600);
	const minutes = Math.floor((totalSeconds % 3600) / 60);
	const seconds = totalSeconds % 60;

	if (hours > 0) {
		return `${hours}h ${minutes}m`;
	}
	return `${minutes}m ${seconds}s`;
}
