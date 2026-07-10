// Presentation helpers shared by the model-fit pages. Kept in a non-component module so the page components can
// import them without tripping the "components-only export" lint rule.

// Badge color for a recommendation fit level. The advisor emits a run-mode fit level of "GPU" (the model fits the
// VRAM budget and runs GPU-accelerated) or "CPU" (estimated against the RAM budget). GPU is the desirable outcome
// (teal); CPU is surfaced separately by the dedicated CPU-mode badge, so anything else falls back to grey.
export function fitLevelColor(fitLevel: string | null): string {
	switch (fitLevel) {
		case "GPU":
			return "teal";
		case "CPU":
			return "orange";
		default:
			return "gray";
	}
}

// Formats an epoch-millis timestamp for display, or a dash when absent.
export function formatModelFitTimestamp(value: number | null): string {
	if (value === null) {
		return "—";
	}
	const date = new Date(value);
	return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString();
}

// Formats a model release date (an ISO date string like "2025-03-12", or an ISO datetime whose leading date we take)
// as a locale-aware, date-only string, or a dash when absent or unparsable. The calendar date is parsed from its
// year/month/day parts and built with the local-time Date constructor — never `new Date("2025-03-12")`, which parses
// as UTC midnight and renders a day earlier in negative-offset time zones. So the shown day always matches the
// publisher's intended release day regardless of the viewer's time zone.
export function formatModelFitReleaseDate(value: string | null): string {
	if (value === null) {
		return "—";
	}
	const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
	if (match === null) {
		return "—";
	}
	const year = Number(match[1]);
	const month = Number(match[2]);
	const day = Number(match[3]);
	const date = new Date(year, month - 1, day);
	// Reject out-of-range parts (e.g. "2025-13-40") that would silently roll over into a different, wrong day.
	if (
		Number.isNaN(date.getTime()) ||
		date.getFullYear() !== year ||
		date.getMonth() !== month - 1 ||
		date.getDate() !== day
	) {
		return "—";
	}
	return date.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

// Formats a numeric metric to a fixed-precision string with an optional unit suffix, or a dash when absent.
export function formatModelFitMetric(value: number | null, unit = "", fractionDigits = 0): string {
	if (value === null) {
		return "—";
	}
	const formatted = value.toFixed(fractionDigits);
	return unit ? `${formatted} ${unit}` : formatted;
}

// Formats a memory figure in megabytes to a compact GB string, or a dash when absent.
export function formatMemoryMb(megabytes: number | null): string {
	if (megabytes === null) {
		return "—";
	}
	return `${(megabytes / 1024).toFixed(1)} GB`;
}

// Formats a context-window token count with thousands separators, or a dash when absent.
export function formatContextTokens(tokens: number | null): string {
	if (tokens === null) {
		return "—";
	}
	return tokens.toLocaleString();
}

// Formats a raw byte count as a compact GB string (one decimal), or a dash when absent. Used by the hardware-profile
// card for RAM / VRAM / disk figures (the wire reports bytes).
export function formatBytesAsGb(bytes: number | null): string {
	if (bytes === null) {
		return "—";
	}
	return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
}
