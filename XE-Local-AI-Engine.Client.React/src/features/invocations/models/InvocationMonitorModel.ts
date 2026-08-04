import type { TFunction } from "i18next";

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
	// True while the turn is parked on an `ask_user` question. Content-free by design — the question text never
	// travels on this ops endpoint, only the fact that the run is waiting on a person.
	hasPendingQuestion: boolean;
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

// UX-12: the fields a plain-language summary needs. Both InvocationCurrentDto (active runs) and InvocationHistoryDto
// (terminal runs) are assignable to this shape — the fields that only exist on the current run (pendingToolCallCount,
// hasPendingApproval, hasPendingQuestion) are optional here so a history row can be summarized too.
export interface InvocationSummaryInput {
	readonly status: InvocationStatusDto;
	readonly modelUsed: string | null;
	readonly durationMs?: number | null;
	readonly streamedChunkCount: number;
	readonly streamedThinkingChunkCount: number;
	readonly pendingToolCallCount?: number;
	readonly hasPendingApproval?: boolean;
	readonly hasPendingQuestion?: boolean;
	readonly error?: string | null;
	readonly failureCategory?: InvocationFailureCategoryDto;
}

// Builds the tool/approval clause: a pending prompt takes precedence (it blocks the run), otherwise the count of
// outstanding tool calls, otherwise a plain "no tool calls" note.
function toolActivityNote(run: InvocationSummaryInput, t: TFunction): string {
	if (run.hasPendingApproval) {
		return t("pages.invocations.monitor.summary.toolApproval", "awaiting tool approval");
	}
	if (run.hasPendingQuestion) {
		return t("pages.invocations.monitor.summary.toolQuestion", "awaiting an answer to a question");
	}
	const pending = run.pendingToolCallCount ?? 0;
	if (pending > 0) {
		return t("pages.invocations.monitor.summary.toolPending", "{{count}} pending tool call(s)", { count: pending });
	}
	return t("pages.invocations.monitor.summary.toolNone", "no tool calls");
}

/**
 * UX-12: renders one localized, human-readable sentence describing an invocation run (verb from status, duration,
 * model, streamed chunk counts, a tool/approval note, and an error note on failure). Pure i18n interpolation so it is
 * unit-testable; the caller passes its `t`. Works for both the active current run and terminal history rows.
 */
export function buildInvocationSummary(run: InvocationSummaryInput, t: TFunction): string {
	const model = formatInvocationText(run.modelUsed);
	const duration = formatInvocationDuration(run.durationMs);
	const chunks = run.streamedChunkCount;
	const thinking = run.streamedThinkingChunkCount;

	switch (run.status) {
		case "Pending":
		case "Assigned":
			return t("pages.invocations.monitor.summary.pending", "Waiting to start on {{model}}.", { model });
		case "Running":
			return t(
				"pages.invocations.monitor.summary.running",
				"Running on {{model}} — {{chunks}} output chunks, {{thinking}} reasoning so far ({{tools}}).",
				{ model, chunks, thinking, tools: toolActivityNote(run, t) },
			);
		case "Completed":
			return t(
				"pages.invocations.monitor.summary.completed",
				"Completed in {{duration}} with {{model}} — {{chunks}} output chunks, {{thinking}} reasoning, {{tools}}.",
				{ duration, model, chunks, thinking, tools: toolActivityNote(run, t) },
			);
		case "Failed":
			return t("pages.invocations.monitor.summary.failed", "Failed after {{duration}} with {{model}} — {{reason}}.", {
				duration,
				model,
				reason: formatInvocationText(run.error ?? run.failureCategory),
			});
		case "Cancelled":
			return t("pages.invocations.monitor.summary.cancelled", "Cancelled after {{duration}} with {{model}}.", {
				duration,
				model,
			});
		default:
			return t("pages.invocations.monitor.summary.unknown", "Invocation status is unknown.");
	}
}
