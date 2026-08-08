import type { ScheduledRunStatus } from "@/features/scheduler/models/SchedulerModels";

// Presentation helpers shared by the run-history table and the run-detail panel. Kept in a non-component module
// so both components can import them without tripping the "components-only export" lint rule.

// Badge color per run status. Active states are blue/grape; success green; the failure family red; the rest grey.
export function scheduledRunStatusColor(status: ScheduledRunStatus): string {
	switch (status) {
		case "Succeeded":
			return "green";
		case "Failed":
		case "TimedOut":
			return "red";
		case "Cancelled":
			return "orange";
		case "Running":
			return "blue";
		case "Queued":
			return "grape";
		default:
			return "gray";
	}
}

// Formats an epoch-millis timestamp for display, or a dash when absent.
export function formatRunTimestamp(value: number | null): string {
	if (value === null) {
		return "—";
	}
	const date = new Date(value);
	return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString();
}

// Formats a duration in milliseconds to a compact seconds string, or a dash when absent.
export function formatRunDuration(durationMs: number | null): string {
	if (durationMs === null) {
		return "—";
	}
	return `${(durationMs / 1000).toFixed(1)}s`;
}
