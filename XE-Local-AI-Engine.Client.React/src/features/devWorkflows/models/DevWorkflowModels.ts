// Domain vocabulary for the Development Workflows surface. The generated client types every enum as a bare `string`
// (they cross the wire as enum NAMES), so these unions are the client-side narrowing — and the place a status typo
// fails to compile instead of falling silently through a colour map. Same shape as `WorkSessionModels.ts`.
//
// Two narrowing shapes, deliberately:
//   `to*`  — a render must pick SOMETHING, so an unknown value falls back to the state with no controls.
//   `as*`  — an unknown value has no honest substitute (a decision kind, an artifact kind), so it reads as
//            `undefined` and the caller displays the raw token instead of mislabelling it.

import type { TFunction } from "i18next";

import type {
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowArtifactResponse as DevWorkflowArtifactResponse,
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowDecisionResponse as DevWorkflowDecisionResponse,
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowDefinitionSummaryResponse as DevWorkflowDefinitionSummaryResponse,
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowGraph as DevWorkflowGraph,
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowGraphEdge as DevWorkflowGraphEdge,
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowGraphNode as DevWorkflowGraphNode,
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowNodeRunDetailResponse as DevWorkflowNodeRunDetailResponse,
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowNodeRunSummaryResponse as DevWorkflowNodeRunSummaryResponse,
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowRuleSetResponse as DevWorkflowRuleSetResponse,
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowRuleSetSummaryResponse as DevWorkflowRuleSetSummaryResponse,
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowRunEventResponse as DevWorkflowRunEventResponse,
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowRunResponse as DevWorkflowRunResponse,
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowRunSummaryResponse as DevWorkflowRunSummaryResponse,
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowWorkItemResponse as DevWorkflowWorkItemResponse,
	XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowWorkItemSummaryResponse as DevWorkflowWorkItemSummaryResponse,
} from "@/core/api/generated/types.gen";

export type {
	DevWorkflowArtifactResponse,
	DevWorkflowDecisionResponse,
	DevWorkflowDefinitionSummaryResponse,
	DevWorkflowGraph,
	DevWorkflowGraphEdge,
	DevWorkflowGraphNode,
	DevWorkflowNodeRunDetailResponse,
	DevWorkflowNodeRunSummaryResponse,
	DevWorkflowRuleSetResponse,
	DevWorkflowRuleSetSummaryResponse,
	DevWorkflowRunEventResponse,
	DevWorkflowRunResponse,
	DevWorkflowRunSummaryResponse,
	DevWorkflowWorkItemResponse,
	DevWorkflowWorkItemSummaryResponse,
};

/** X4, canonical. `Blocked` is needs-intervention (Y20), NOT a dependency wait — that is `Pending`. */
export const devWorkflowNodeStatuses = [
	"Pending",
	"Queued",
	"Running",
	"WaitingForApproval",
	"Blocked",
	"Succeeded",
	"Failed",
	"Skipped",
	"Cancelled",
] as const;
export type DevWorkflowNodeStatus = (typeof devWorkflowNodeStatuses)[number];

/** X4, canonical. No `Interrupted`: runs auto-resume after an engine restart, only node-runs reconcile. */
export const devWorkflowRunStatuses = [
	"Pending",
	"Running",
	"Pausing",
	"Paused",
	"WaitingForApproval",
	"Cancelling",
	"Completed",
	"Failed",
	"Cancelled",
] as const;
export type DevWorkflowRunStatus = (typeof devWorkflowRunStatuses)[number];

/** Y6: SEVEN members. Start/End are implicit — entry = no inbound edges, terminal = no outbound. */
export const devWorkflowNodeTypes = ["Agent", "Tool", "DevTask", "HumanGate", "Gate", "Parallel", "Join"] as const;
export type DevWorkflowNodeType = (typeof devWorkflowNodeTypes)[number];

/** X3: gates use the first three, retries-exhausted interventions the rest — one enum, one endpoint. */
export const devWorkflowDecisionKinds = ["Approve", "Reject", "RequestChanges", "Retry", "Skip", "Abandon"] as const;
export type DevWorkflowDecisionKind = (typeof devWorkflowDecisionKinds)[number];

/** P1's ten, verbatim (C24/Y25) — not the work-session set. */
export const devWorkflowArtifactKinds = [
	"Research",
	"Decision",
	"Specification",
	"Plan",
	"TaskPackage",
	"Patch",
	"Report",
	"Finding",
	"ValidationReport",
	"ReviewReport",
] as const;
export type DevWorkflowArtifactKind = (typeof devWorkflowArtifactKinds)[number];

/**
 * The detail page's tab selection, carried in ONE `?tab=` search param across two Tabs components (ruled):
 * `graph`/`nodes` select the CENTRE pane, `artifacts`/`events` the side pane. Each component derives its own value with
 * its own default — `graph` for the centre, `artifacts` for the side — so an absent or foreign-pane value simply reads
 * as that pane's default rather than blanking it. One param, because a URL that carries two tab states for a surface
 * that shows both at once is two params nobody would ever set independently.
 */
export const devWorkflowDetailTabs = ["artifacts", "events", "graph", "nodes"] as const;
export type DevWorkflowDetailTab = (typeof devWorkflowDetailTabs)[number];

/** Y4: written by the runtime, never by the client. A `Failed` run maps here to `Blocked` — it needs attention. */
export const devWorkflowWorkItemStatuses = ["Draft", "Active", "Blocked", "Completed", "Cancelled"] as const;
export type DevWorkflowWorkItemStatus = (typeof devWorkflowWorkItemStatuses)[number];

function narrow<T extends string>(values: readonly T[], value: string | undefined | null, fallback: T): T {
	return values.includes(value as T) ? (value as T) : fallback;
}

function asMember<T extends string>(values: readonly T[], value: string | undefined | null): T | undefined {
	return values.includes(value as T) ? (value as T) : undefined;
}

/** An unknown node status reads as `Pending`: the state with no controls and no motion. */
export function toDevWorkflowNodeStatus(value: string | undefined | null): DevWorkflowNodeStatus {
	return narrow(devWorkflowNodeStatuses, value, "Pending");
}

/** An unknown run status reads as `Pending`: never started, no lifecycle commands offered. */
export function toDevWorkflowRunStatus(value: string | undefined | null): DevWorkflowRunStatus {
	return narrow(devWorkflowRunStatuses, value, "Pending");
}

/**
 * An unknown node type reads as `Gate` — the structural, read-only panel. Falling back to `Agent` would offer a
 * work-session link-out for a node that has no session, and to `DevTask` a Dev Mode deep link that resolves nothing.
 */
export function toDevWorkflowNodeType(value: string | undefined | null): DevWorkflowNodeType {
	return narrow(devWorkflowNodeTypes, value, "Gate");
}

/** An unknown work-item status reads as `Draft`: nothing has run, nothing is offered. */
export function toDevWorkflowWorkItemStatus(value: string | undefined | null): DevWorkflowWorkItemStatus {
	return narrow(devWorkflowWorkItemStatuses, value, "Draft");
}

/**
 * `undefined`, not a fallback: a decision kind drives a BUTTON. An unrecognised token rendered as `Approve` would
 * offer an operator a control that means something else, so it is dropped and the raw token is displayed instead.
 */
export function asDevWorkflowDecisionKind(value: string | undefined | null): DevWorkflowDecisionKind | undefined {
	return asMember(devWorkflowDecisionKinds, value);
}

/** `allowedDecisions` filtered to members this client can actually render. Order follows the server's. */
export function toDevWorkflowDecisionKinds(values: readonly string[] | undefined | null): readonly DevWorkflowDecisionKind[] {
	return (values ?? []).flatMap((value) => {
		const kind = asDevWorkflowDecisionKind(value);
		return kind ? [kind] : [];
	});
}

/** `undefined` for an unknown kind: the badge shows the raw token rather than mislabelling it as a `Report`. */
export function asDevWorkflowArtifactKind(value: string | undefined | null): DevWorkflowArtifactKind | undefined {
	return asMember(devWorkflowArtifactKinds, value);
}

/** Nothing more will happen on this run without a new one being started. */
export function isTerminalDevWorkflowRunStatus(status: DevWorkflowRunStatus): boolean {
	return status === "Completed" || status === "Failed" || status === "Cancelled";
}

/**
 * The run is live, so the list polls (X16 Q7) and the toolbar offers lifecycle commands. `Pausing`/`Cancelling` count:
 * the commands are fire-and-forget and work is still winding down behind them.
 */
export function isActiveDevWorkflowRunStatus(status: DevWorkflowRunStatus): boolean {
	return !isTerminalDevWorkflowRunStatus(status);
}

/**
 * The run has stopped and a human is the only thing that restarts it. Both a gate and an exhausted-retry
 * intervention land here (Y20) — the count that matters is `pendingDecisionCount`, never this predicate.
 */
export function devWorkflowNodeAwaitsHuman(status: DevWorkflowNodeStatus): boolean {
	return status === "WaitingForApproval" || status === "Blocked";
}

/**
 * A Tool node that LANDS patches rather than judging them (R-C3). The stored spelling is the parser's own canonical
 * `"Apply"`, but it crosses the wire as a bare string and a definition authored by hand may carry any casing — so the
 * comparison is case-insensitive, exactly as the server's own `Enum.TryParse(..., ignoreCase: true)` is. Absent means
 * `Validate`, which is the server's default too.
 */
export function isDevWorkflowApplyToolMode(toolMode: string | undefined | null): boolean {
	return (toolMode ?? "").toLowerCase() === "apply";
}

/**
 * The node run has stopped for good on this attempt: the runtime has routed past it, so its successors carry the
 * states that say which branch it took. A gate that has not reached one of these has nothing settled to show.
 */
export function isSettledDevWorkflowNodeStatus(status: DevWorkflowNodeStatus): boolean {
	return status === "Succeeded" || status === "Failed" || status === "Skipped" || status === "Cancelled";
}

/** Only `Running` earns motion. `Queued` deliberately does not — see the O9 honesty rule in the table. */
export function isDevWorkflowNodeInProgress(status: DevWorkflowNodeStatus): boolean {
	return status === "Running";
}

/**
 * Monaco language for a workflow artifact. Patches render as a diff, task packages as JSON, and everything else
 * follows its media type with markdown as the default — the ten kinds are overwhelmingly markdown documents.
 */
export function devWorkflowArtifactLanguage(kind: DevWorkflowArtifactKind | undefined, mediaType: string | undefined): string {
	if (kind === "Patch") {
		return "diff";
	}
	if (kind === "TaskPackage") {
		return "json";
	}
	const type = (mediaType ?? "").toLowerCase();
	if (type.includes("json")) {
		return "json";
	}
	if (type.includes("patch") || type.includes("diff")) {
		return "diff";
	}
	if (type.includes("xml") || type.includes("html")) {
		return "xml";
	}
	if (type.includes("plain")) {
		return "plaintext";
	}
	return "markdown";
}

/** One artifact identity across its versions (X6/Y15: the lineage is `(RunId, ProducingNodeKey, Name)`). */
export interface DevWorkflowArtifactLineage {
	readonly lineageId: string;
	/** Every version of this lineage, NEWEST first — the order the version picker offers them in. */
	readonly versions: readonly DevWorkflowArtifactResponse[];
	/** The row the server marked `isLatest` (Y17/C39), or the highest version when no row claims it. */
	readonly latest: DevWorkflowArtifactResponse;
}

/**
 * The run's artifact feed grouped into lineages, in first-appearance order.
 *
 * Grouping is by the SERVER's `lineageId` and nothing else: `name + kind` would silently merge a renamed artifact
 * with an unrelated one, which is the whole reason X6 put the field on the wire. `isLatest` is likewise READ rather
 * than derived (Y17/C39) — the query already computes it, and a second client-side reduce would be a second answer
 * to one question. The `version` sort is only the order the picker lists them in.
 *
 * A row with no lineage id becomes its own lineage rather than joining a shared empty-string bucket: collapsing
 * unrelated documents into one row would hand the operator a version picker that switches between different files.
 */
export function devWorkflowArtifactLineages(
	artifacts: readonly DevWorkflowArtifactResponse[],
): readonly DevWorkflowArtifactLineage[] {
	const byLineage = new Map<string, DevWorkflowArtifactResponse[]>();
	for (const artifact of artifacts) {
		const key = artifact.lineageId || `artifact:${artifact.id ?? ""}`;
		const rows = byLineage.get(key);
		if (rows) {
			rows.push(artifact);
		} else {
			byLineage.set(key, [artifact]);
		}
	}
	return [...byLineage.entries()].flatMap(([lineageId, rows]) => {
		const versions = rows.toSorted((left, right) => (right.version ?? 1) - (left.version ?? 1));
		const latest = versions.find((row) => row.isLatest === true) ?? versions[0];
		return latest ? [{ lineageId, versions, latest }] : [];
	});
}

/**
 * The attempt numbers, for the three surfaces that render that sentence.
 *
 * Two maxima, because after FU2-3 there are two: an operator's Retry on an exhausted node widens the node run's
 * `maxAttempts` by one per Retry, so `maxAttempts` is the budget the runtime is now working to and `cap` the one the
 * definition declared. They differ exactly when `operatorRetries` is non-zero, which is when the copy has to say a
 * human granted the extra attempt rather than let the badge imply the runtime overran its own budget.
 *
 * The clamp up to the attempt is now only a compatibility guard: a server from before the widening still sends
 * `attempt` past `maxAttempts` on an operator retry, and "attempt 4 of 3" is a sentence about a broken runtime.
 */
export function devWorkflowAttemptCounts(
	attempt: number | undefined | null,
	maxAttempts: number | undefined | null,
	operatorRetries?: number | undefined | null,
): DevWorkflowAttemptCounts {
	const current = attempt ?? 1;
	const effective = Math.max(current, maxAttempts ?? 1);
	const retries = Math.max(0, operatorRetries ?? 0);
	return { attempt: current, maxAttempts: effective, cap: Math.max(1, effective - retries), operatorRetries: retries };
}

export interface DevWorkflowAttemptCounts {
	readonly attempt: number;
	/** The budget in force, operator retries included. */
	readonly maxAttempts: number;
	/** The budget the definition declared, never below 1. */
	readonly cap: number;
	readonly operatorRetries: number;
}

/**
 * The rendered attempt sentence. One helper rather than the same branch at each of the three surfaces.
 *
 * The retry wording describes GRANTED CAPACITY, never who started the attempt on screen. `operatorRetries` is a
 * cumulative delta on the row, so a Retry taken before the cap was reached widens the budget for every later attempt
 * — including the ordinary automatic ones. "attempt 3 (operator retry)" would then credit a human with an attempt the
 * runtime started by itself. So both halves stay visible: the ordinary "N of M", plus what the definition declared
 * and how much an operator added to it.
 */
export function devWorkflowAttemptLabel(t: TFunction, counts: DevWorkflowAttemptCounts): string {
	return counts.operatorRetries > 0
		? t(
				"pages.devWorkflows.nodes.attemptOperatorRetry",
				"attempt {{attempt}} of {{maxAttempts}} (cap {{cap}}, +1 from an operator retry)",
				{ ...counts, count: counts.operatorRetries },
			)
		: t("pages.devWorkflows.nodes.attempt", "attempt {{attempt}} of {{maxAttempts}}", { ...counts });
}

/** The shared decoder (P4 §2.10), kept under the feature's own name so no call site or test had to move. */
export { decodeArtifactContent as decodeDevWorkflowArtifactContent } from "@/core/artifacts/ArtifactContent";
