// Attempt history, derived from the event log (X2 / P4 §2.6). There is ONE node-run row per `(RunId, NodeKey)` with
// `Attempt` incrementing in place, so the row cannot answer "what happened on attempt 2" — only "which attempt is it
// on now". The event feed can: `node.retry.scheduled` is the only reason a node moves back to `Pending`, so each one
// closes an attempt and opens the next, and `worksession.attached` names the session each attempt ran in.
//
// The walk is over ASCENDING sequences and counts from 1. Where an event states its own attempt number
// (`worksession.attached` carries one) that number wins over the counter — the log is the authority, and the counter
// is only there for the events that carry no attempt at all.

import type { DevWorkflowRunEventResponse } from "@/features/devWorkflows/models/DevWorkflowModels";

/** The subset of the 30-token catalog this module reads. Every one of them is attached to a node run. */
export const devWorkflowAttemptEventTypes = {
	retryScheduled: "node.retry.scheduled",
	retryRouted: "node.retry.routed",
	workSessionAttached: "worksession.attached",
	interrupted: "node.interrupted",
	completed: "node.completed",
	failed: "node.failed",
	skipped: "node.skipped",
	cancelled: "node.cancelled",
} as const;

/** Ends an attempt. A retry closes one and opens the next; the other four close the last one for good. */
const closingEventTypes: readonly string[] = [
	devWorkflowAttemptEventTypes.retryScheduled,
	devWorkflowAttemptEventTypes.completed,
	devWorkflowAttemptEventTypes.failed,
	devWorkflowAttemptEventTypes.skipped,
	devWorkflowAttemptEventTypes.cancelled,
];

export interface DevWorkflowNodeAttempt {
	readonly attempt: number;
	/** The event's own outcome token, rendered verbatim — the client narrows no vocabulary it does not own. */
	readonly outcome?: string;
	/** Which catalog event closed the attempt. Absent on the attempt still running. */
	readonly closedBy?: string;
	readonly workSessionId?: string;
	readonly occurredAtUtc?: number;
	/** `node.interrupted` rows for this attempt — the restart evidence `sessionResumes` does NOT carry. */
	readonly interruptedCount: number;
}

interface MutableAttempt {
	attempt: number;
	outcome?: string;
	closedBy?: string;
	workSessionId?: string;
	occurredAtUtc?: number;
	interruptedCount: number;
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null;
}

/** `worksession.attached` carries `{workSessionId, attempt, sessionResumes}`; anything else reads as absent. */
function attachedDetail(detailJson: string | null | undefined): { workSessionId?: string; attempt?: number } {
	if (typeof detailJson !== "string" || detailJson.length === 0) {
		return {};
	}
	let parsed: unknown;
	try {
		parsed = JSON.parse(detailJson);
	} catch {
		return {};
	}
	if (!isRecord(parsed)) {
		return {};
	}
	return {
		workSessionId: typeof parsed["workSessionId"] === "string" ? parsed["workSessionId"] : undefined,
		attempt: typeof parsed["attempt"] === "number" ? parsed["attempt"] : undefined,
	};
}

/** `node.retry.routed` carries `{nodeKey, retryTarget, failureClass, reason}` — the node that FAILED, not the reset one. */
export function devWorkflowRoutedDetail(detailJson: string | null | undefined): { nodeKey?: string; retryTarget?: string } {
	if (typeof detailJson !== "string" || detailJson.length === 0) {
		return {};
	}
	let parsed: unknown;
	try {
		parsed = JSON.parse(detailJson);
	} catch {
		return {};
	}
	if (!isRecord(parsed)) {
		return {};
	}
	return {
		nodeKey: typeof parsed["nodeKey"] === "string" ? parsed["nodeKey"] : undefined,
		retryTarget: typeof parsed["retryTarget"] === "string" ? parsed["retryTarget"] : undefined,
	};
}

export function devWorkflowNodeEvents(
	events: readonly DevWorkflowRunEventResponse[],
	nodeRunId: string | undefined,
): readonly DevWorkflowRunEventResponse[] {
	if (!nodeRunId) {
		return [];
	}
	return events
		.filter((event) => event.nodeRunId === nodeRunId)
		.toSorted((left, right) => (left.sequence ?? 0) - (right.sequence ?? 0));
}

/**
 * One row per attempt, oldest first.
 *
 * `currentAttempt` is the node-run row's own number and is the authority on how many attempts there have been: the
 * loaded event pages may not reach back far enough to have seen every retry, and a list that stopped at attempt 1
 * because page two was never fetched would read as "this node never retried". Attempts the feed cannot account for
 * are still listed, carrying nothing — an attempt with no evidence loaded, said plainly.
 */
export function devWorkflowNodeAttempts(
	nodeEvents: readonly DevWorkflowRunEventResponse[],
	currentAttempt: number,
): readonly DevWorkflowNodeAttempt[] {
	const byAttempt = new Map<number, MutableAttempt>();
	const at = (attempt: number): MutableAttempt => {
		const existing = byAttempt.get(attempt);
		if (existing) {
			return existing;
		}
		const created: MutableAttempt = { attempt, interruptedCount: 0 };
		byAttempt.set(attempt, created);
		return created;
	};

	let counter = 1;
	for (const event of nodeEvents) {
		const type = event.eventType ?? "";
		if (type === devWorkflowAttemptEventTypes.workSessionAttached) {
			const detail = attachedDetail(event.detailJson);
			const row = at(detail.attempt ?? counter);
			row.workSessionId = detail.workSessionId;
			continue;
		}
		if (type === devWorkflowAttemptEventTypes.interrupted) {
			at(counter).interruptedCount += 1;
			continue;
		}
		if (closingEventTypes.includes(type)) {
			const row = at(counter);
			row.outcome = event.outcome ?? undefined;
			row.closedBy = type;
			row.occurredAtUtc = event.occurredAtUtc;
			if (type === devWorkflowAttemptEventTypes.retryScheduled) {
				counter += 1;
			}
		}
	}

	const highest = Math.max(counter, currentAttempt, ...byAttempt.keys());
	return Array.from({ length: Math.max(highest, 1) }, (_, index) => {
		const row = byAttempt.get(index + 1);
		return row ? { ...row } : { attempt: index + 1, interruptedCount: 0 };
	});
}
