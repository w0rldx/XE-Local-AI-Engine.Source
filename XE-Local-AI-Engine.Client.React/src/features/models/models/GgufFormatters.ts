// Presentation helpers for the GGUF browse + download flow. Kept in a non-component module so the components can
// import them without tripping the "components-only export" lint rule.

// Formats an epoch-millis timestamp for display, or a dash when absent.
export function formatGgufTimestamp(value: number | null): string {
	if (value === null) {
		return "—";
	}
	const date = new Date(value);
	return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString();
}

// Formats a raw byte count as a compact GB string (one decimal), or a dash when absent. Used by the quant picker's
// size column (the wire reports bytes).
export function formatBytesAsGb(bytes: number | null): string {
	if (bytes === null) {
		return "—";
	}
	return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
}
