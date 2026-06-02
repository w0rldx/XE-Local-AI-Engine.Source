// Presentation helpers shared by the model-fit pages. Kept in a non-component module so the page components can
// import them without tripping the "components-only export" lint rule.

// Badge color for a recommendation fit level (llmfit values: Perfect / Good / Marginal / Too Tight). Unknown or
// missing levels fall back to grey.
export function fitLevelColor(fitLevel: string | null): string {
	switch (fitLevel) {
		case "Perfect":
			return "green";
		case "Good":
			return "teal";
		case "Marginal":
			return "yellow";
		case "Too Tight":
			return "red";
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
