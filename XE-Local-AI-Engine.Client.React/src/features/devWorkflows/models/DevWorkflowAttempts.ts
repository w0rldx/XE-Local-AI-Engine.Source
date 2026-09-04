// Attempt history, derived from the event log (X2 / P4 §2.6). There is ONE node-run row per `(RunId, NodeKey)` with
// `Attempt` incrementing in place, so the row cannot answer "what happened on attempt 2" — only "which attempt is it
// on now". The event feed can: `node.retry.scheduled` is the only reason a node moves back to `Pending`, so each one
// closes an attempt and opens the next, and `worksession.attached` names the session each attempt ran in.
//
// The walk is over ASCENDING sequences and counts from 1. Where an event states its own attempt number — the two that
// do are `worksession.attached` and `node.retry.scheduled` — that number wins over the counter AND moves it, because
// the counter is only a fallback for the events carrying no attempt at all, and a page set anchored to the tail can
// start after the retries that would have grown it. It never moves backwards.

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

/**
 * The nine ADDITIVE members the settled node-run row carries — the same names on a `node.retry.scheduled` payload, so
 * a total is `final + retries` and nothing else. `servedModelName`, `toolNames` and `route` are deliberately NOT here:
 * they describe one attempt's shape rather than a quantity, and carrying them per attempt would invite a sum that
 * means nothing. The retry payload carries a TENTH additive member, `modelReadinessMs`, which the panel does not read
 * until the node-run detail DTO exposes it — without the final attempt's share it could only ever be a partial sum.
 */
export interface DevWorkflowAttemptCost {
	readonly inputTokens?: number | null;
	readonly outputTokens?: number | null;
	readonly reasoningTokens?: number | null;
	readonly estimatedInputTokens?: number | null;
	readonly providerCalls?: number | null;
	readonly toolCalls?: number | null;
	readonly toolSchemaTokens?: number | null;
	readonly agentTurnMs?: number | null;
	readonly workSessionSteps?: number | null;
}

/** Read both to populate an attempt and to ask whether one recorded anything at all. */
export const devWorkflowAttemptCostFields = [
	"inputTokens",
	"outputTokens",
	"reasoningTokens",
	"estimatedInputTokens",
	"providerCalls",
	"toolCalls",
	"toolSchemaTokens",
	"agentTurnMs",
	"workSessionSteps",
] as const satisfies readonly (keyof DevWorkflowAttemptCost)[];

export interface DevWorkflowNodeAttempt extends DevWorkflowAttemptCost {
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

type MutableAttempt = { -readonly [K in keyof DevWorkflowNodeAttempt]: DevWorkflowNodeAttempt[K] };

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null;
}

/** An event's `detailJson` as an object. Absent, empty and unparseable all read the same: nothing was stated. */
function detailOf(detailJson: string | null | undefined): Record<string, unknown> {
	if (typeof detailJson !== "string" || detailJson.length === 0) {
		return {};
	}
	let parsed: unknown;
	try {
		parsed = JSON.parse(detailJson);
	} catch {
		return {};
	}
	return isRecord(parsed) ? parsed : {};
}

/**
 * One field of a detail payload, under either spelling the event log holds.
 *
 * The store serialized its own payloads with the framework default until FX-D, so rows written before that carry
 * `{"WorkSessionId":…,"Attempt":1}` while everything since — and every payload the Application layer ever wrote —
 * is camelCase. The log is APPEND-ONLY: those rows keep their spelling for ever, so a reader that takes only the
 * new one silently reports nothing for every run that already exists.
 */
function field(detail: Record<string, unknown>, name: string): unknown {
	return detail[name] ?? detail[name[0]!.toUpperCase() + name.slice(1)];
}

/**
 * The attempt an event states about itself, if it states one.
 *
 * Two of the catalog's payloads carry it: `worksession.attached` (the attempt the session was attached to, written
 * by the STORE) and `node.retry.scheduled` (the attempt that FAILED, which is the one that event closes, written by
 * the Application layer). The rest carry a reason or nothing, so reading the field generically cannot pick up a
 * number that means something else.
 */
function statedAttempt(detail: Record<string, unknown>): number | undefined {
	const stated = field(detail, "attempt");
	return typeof stated === "number" ? stated : undefined;
}

/**
 * `node.retry.routed` carries `{from, to, failureClass, reason}` — `from` is the node that FAILED, `to` the one the
 * fix loop resets to. The keys are the server's, verbatim: `RoutedDetail(From, To, …)` under the Web serializer
 * defaults every payload in this file is read with. This used to read `nodeKey`/`retryTarget`, which the server has
 * never written, so both were always undefined and the cascade banner never rendered.
 */
export function devWorkflowRoutedDetail(detailJson: string | null | undefined): { from?: string; to?: string } {
	const detail = detailOf(detailJson);
	return {
		from: typeof detail["from"] === "string" ? detail["from"] : undefined,
		to: typeof detail["to"] === "string" ? detail["to"] : undefined,
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

	// The counter only ever moves FORWARD, to where the log says history is. On a tail-anchored page set the
	// `node.retry.scheduled` events that grew it may not be loaded, so an attachment (or a retry) naming attempt 3 is
	// the log saying where history actually is — and every later event carrying no number of its own would otherwise
	// land on attempt 1. A number BEHIND the counter moves nothing: it is a row already accounted for, and letting it
	// push the counter on by one would skip the attempt in between.
	let counter = 1;
	for (const event of nodeEvents) {
		const type = event.eventType ?? "";
		const detail = detailOf(event.detailJson);
		// The attempt this event belongs to: its own number where it states one, else wherever the walk has reached.
		const attempt = statedAttempt(detail) ?? counter;
		if (type === devWorkflowAttemptEventTypes.workSessionAttached) {
			counter = Math.max(counter, attempt);
			const session = field(detail, "workSessionId");
			at(attempt).workSessionId = typeof session === "string" ? session : undefined;
			continue;
		}
		if (type === devWorkflowAttemptEventTypes.interrupted) {
			at(counter).interruptedCount += 1;
			continue;
		}
		if (closingEventTypes.includes(type)) {
			const row = at(attempt);
			row.outcome = event.outcome ?? undefined;
			row.closedBy = type;
			row.occurredAtUtc = event.occurredAtUtc;
			// What the attempt this event closes actually SPENT. The retry payload is the only place it survives: the
			// node-run row keeps the LAST attempt's numbers and nothing else, so a retried node can otherwise never be
			// added up. Non-numbers are ignored rather than coerced — a payload that stated no number stated nothing.
			if (type === devWorkflowAttemptEventTypes.retryScheduled) {
				for (const name of devWorkflowAttemptCostFields) {
					const value = field(detail, name);
					if (typeof value === "number") {
						row[name] = value;
					}
				}
			}
			// A retry closes this attempt AND opens the next; the other four close it for good.
			counter = Math.max(counter, type === devWorkflowAttemptEventTypes.retryScheduled ? attempt + 1 : attempt);
		}
	}

	const highest = Math.max(counter, currentAttempt, ...byAttempt.keys());
	return Array.from({ length: Math.max(highest, 1) }, (_, index) => {
		const row = byAttempt.get(index + 1);
		return row ? { ...row } : { attempt: index + 1, interruptedCount: 0 };
	});
}
