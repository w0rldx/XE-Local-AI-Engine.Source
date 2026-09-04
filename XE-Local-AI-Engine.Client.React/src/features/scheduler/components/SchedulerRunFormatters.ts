import type { ScheduledRunStatus } from "@/features/scheduler/models/SchedulerModels";

// Presentation helper shared by the run-history table and the run-detail panel. Kept in a non-component module
// so both components can import it without tripping the "components-only export" lint rule. The timestamp and
// duration formatters this file also held now live in `@/core/formatting/TimeFormatting`, shared with integrations.

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
