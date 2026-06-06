import { emptyModelValue, formatModelSize } from "@/features/models/models/LocalModelModel";

// Presentation helpers for the loaded-models page. Kept in a non-component module so the page can import them
// without tripping the "components-only export" lint rule.

// Human-readable memory footprint. Reuses the models feature's formatModelSize so the GB/MB/KB rounding matches
// the rest of the app (e.g. the installed-models table); absent/negative sizes render as the shared em-dash.
export function formatLoadedModelSize(sizeBytes: number | null): string {
	return formatModelSize(sizeBytes);
}

// Compact "expires in" countdown derived from an epoch-millis expiry against `now`. The runtime evicts an idle
// model when its keep-alive timer elapses; this surfaces how long is left. A past/zero expiry reads the localized
// `expiredLabel` the caller supplies (this module is `t`-free by design, so the page injects the translation); an
// absent expiry reads the shared em-dash. Granularity steps down (h → m → s) so the label stays short.
export function formatExpiresIn(expiresAtUtc: number | null, expiredLabel: string, now: number = Date.now()): string {
	if (expiresAtUtc === null || !Number.isFinite(expiresAtUtc)) {
		return emptyModelValue;
	}

	const remainingMs = expiresAtUtc - now;
	if (remainingMs <= 0) {
		return expiredLabel;
	}

	const totalSeconds = Math.floor(remainingMs / 1000);
	const hours = Math.floor(totalSeconds / 3600);
	const minutes = Math.floor((totalSeconds % 3600) / 60);
	const seconds = totalSeconds % 60;

	if (hours > 0) {
		return `${hours}h ${minutes}m`;
	}
	if (minutes > 0) {
		return `${minutes}m ${seconds}s`;
	}
	return `${seconds}s`;
}
