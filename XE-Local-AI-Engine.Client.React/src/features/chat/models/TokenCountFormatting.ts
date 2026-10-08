// Compact token count for the context badge and its popover: 950, 3.1k, 1.2m; an absent value renders as a dash.
function trimNumber(value: number): string {
	return value.toFixed(1).replace(/\.0$/, "");
}

export function formatTokenCount(value: number | undefined): string {
	if (value === undefined) {
		return "—";
	}

	if (value >= 1_000_000) {
		return `${trimNumber(value / 1_000_000)}m`;
	}

	if (value >= 1_000) {
		return `${trimNumber(value / 1_000)}k`;
	}

	return value.toString();
}
