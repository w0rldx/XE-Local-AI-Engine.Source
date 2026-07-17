// Domain view-models for the invocation monitor. The generated OpenAPI responses are the single source of truth
// for the wire shape; their fields are all optional. These stricter types (every field required) are what the
// page and the pure helpers below depend on — the mappers in InvocationMonitorMappers.ts coalesce each optional
// generated field to a required value. InvocationStatus mirrors the generated InvocationStatus enum (a string
// union with the same values); FailureCategory surfaces as a plain string so display degrades gracefully.
export type InvocationStatusDto = "Pending" | "Assigned" | "Running" | "Completed" | "Failed" | "Cancelled";

export type InvocationFailureCategoryDto = string | null;

export interface InvocationCurrentDto {
	invocationId: string;
	conversationId: string;
	status: InvocationStatusDto;
	modelUsed: string | null;
	startedAt: string;
	lastUpdatedAt: string;
	completedAt: string | null;
	error: string | null;
	failureCategory: InvocationFailureCategoryDto;
	streamedChunkCount: number;
	streamedThinkingChunkCount: number;
	pendingToolCallCount: number;
	hasPendingApproval: boolean;
	// W3C trace id of the run (AUD4-19), for correlating a failed row's "See local logs" line with exported traces.
	// Null when no activity was in scope. Rendered as copyable text.
	traceId: string | null;
}

export interface InvocationHistoryDto {
	invocationId: string;
	conversationId: string;
	status: InvocationStatusDto;
	modelUsed: string | null;
	startedAt: string;
	completedAt: string;
	durationMs: number;
	error: string | null;
	failureCategory: InvocationFailureCategoryDto;
	streamedChunkCount: number;
	streamedThinkingChunkCount: number;
	// W3C trace id of the run (AUD4-19); null when no activity was in scope. Rendered as copyable text.
	traceId: string | null;
}

export interface InvocationMonitorDto {
	current: InvocationCurrentDto | null;
	history: InvocationHistoryDto[];
	historyCapacity: number;
}

const invocationEmptyValue = "—";

export function formatInvocationText(value: string | null | undefined): string {
	return value?.trim() || invocationEmptyValue;
}

export function formatInvocationTimestamp(value: string | null | undefined): string {
	if (!value) {
		return "Not reported";
	}

	const date = new Date(value);
	if (Number.isNaN(date.getTime()) || date.getTime() === 0) {
		return "Not reported";
	}

	return date.toLocaleString();
}

export function formatInvocationDuration(durationMs: number | null | undefined): string {
	if (durationMs === null || durationMs === undefined || !Number.isFinite(durationMs) || durationMs < 0) {
		return invocationEmptyValue;
	}

	if (durationMs >= 60_000) {
		return `${(durationMs / 60_000).toFixed(1)} min`;
	}

	if (durationMs >= 1000) {
		return `${(durationMs / 1000).toFixed(1)} s`;
	}

	return `${Math.round(durationMs)} ms`;
}

export function getInvocationStatusColor(status: InvocationStatusDto | undefined): "blue" | "green" | "gray" | "red" | "yellow" {
	switch (status) {
		case "Assigned":
		case "Running":
			return "blue";
		case "Completed":
			return "green";
		case "Failed":
			return "red";
		case "Cancelled":
			return "yellow";
		default:
			return "gray";
	}
}

export function isInvocationActive(status: InvocationStatusDto | undefined): boolean {
	return status === "Assigned" || status === "Running";
}

export function sortInvocationHistory(history: InvocationHistoryDto[]): InvocationHistoryDto[] {
	return history.toSorted((left, right) => new Date(right.completedAt).getTime() - new Date(left.completedAt).getTime());
}
