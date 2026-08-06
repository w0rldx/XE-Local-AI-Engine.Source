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
