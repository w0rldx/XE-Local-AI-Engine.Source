// Domain vocabulary for the Graph Workflows surface, and the ONE file that names a generated DTO: every other file in
// this feature imports the bare aliases from here, so a regenerated client is a one-file edit. Same shape as
// `DevWorkflowModels.ts`, which this is copy-adapted from (features never import each other — see `no-cross-feature`).
//
// The generated client types every enum as a bare `string` (they cross the wire as enum NAMES via `ToString()`), so
// these unions are the client-side narrowing — and the place a status typo fails to compile instead of falling
// silently through a colour map.
//
// Two narrowing shapes, deliberately:
//   `narrow*` — a render must pick SOMETHING, so an unknown value falls back to the most inert state.
//   `asMember*` — an unknown value has no honest substitute (a decision kind drives a BUTTON), so it reads as
//                 `undefined` and the caller displays the raw token rather than mislabelling it.

import type {
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1CreateGraphWorkflowDefinitionRequest as CreateGraphWorkflowDefinitionRequest,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1DecideGraphWorkflowNodeRunRequest as DecideGraphWorkflowNodeRunRequest,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowDecisionResultResponse as GraphWorkflowDecisionResultResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowDefinitionResponse as GraphWorkflowDefinitionResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowDefinitionSummaryResponse as GraphWorkflowDefinitionSummaryResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowEdgeCondition as GraphWorkflowEdgeCondition,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowGraph as GraphWorkflowGraph,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowGraphEdge as GraphWorkflowGraphEdge,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowGraphNode as GraphWorkflowGraphNode,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowNodePosition as GraphWorkflowNodePosition,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowNodeRunResponse as GraphWorkflowNodeRunResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowNodeRunSummaryResponse as GraphWorkflowNodeRunSummaryResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowRunEventResponse as GraphWorkflowRunEventResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowRunResponse as GraphWorkflowRunResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowRunSummaryResponse as GraphWorkflowRunSummaryResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowToolResponse as GraphWorkflowToolResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowValidationErrorResponse as GraphWorkflowValidationErrorResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1ListGraphWorkflowDefinitionsResponse as ListGraphWorkflowDefinitionsResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1ListGraphWorkflowRunEventsResponse as ListGraphWorkflowRunEventsResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1ListGraphWorkflowRunsResponse as ListGraphWorkflowRunsResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1ListGraphWorkflowToolsResponse as ListGraphWorkflowToolsResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1StartGraphWorkflowRunRequest as StartGraphWorkflowRunRequest,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1StartGraphWorkflowRunResponse as StartGraphWorkflowRunResponse,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1UpdateGraphWorkflowDefinitionRequest as UpdateGraphWorkflowDefinitionRequest,
	XeLocalAiEngineClientEndpointsGraphWorkflowsV1ValidateGraphWorkflowDefinitionResponse as ValidateGraphWorkflowDefinitionResponse,
} from "@/core/api/generated/types.gen";

export type {
	CreateGraphWorkflowDefinitionRequest,
	DecideGraphWorkflowNodeRunRequest,
	GraphWorkflowDecisionResultResponse,
	GraphWorkflowDefinitionResponse,
	GraphWorkflowDefinitionSummaryResponse,
	GraphWorkflowEdgeCondition,
	GraphWorkflowGraph,
	GraphWorkflowGraphEdge,
	GraphWorkflowGraphNode,
	GraphWorkflowNodePosition,
	GraphWorkflowNodeRunResponse,
	GraphWorkflowNodeRunSummaryResponse,
	GraphWorkflowRunEventResponse,
	GraphWorkflowRunResponse,
	GraphWorkflowRunSummaryResponse,
	GraphWorkflowToolResponse,
	GraphWorkflowValidationErrorResponse,
	ListGraphWorkflowDefinitionsResponse,
	ListGraphWorkflowRunEventsResponse,
	ListGraphWorkflowRunsResponse,
	ListGraphWorkflowToolsResponse,
	StartGraphWorkflowRunRequest,
	StartGraphWorkflowRunResponse,
	UpdateGraphWorkflowDefinitionRequest,
	ValidateGraphWorkflowDefinitionResponse,
};

/** The eight v1 node kinds. A closed vocabulary: an unknown `kind` is a save-time validation error server-side. */
export const graphWorkflowNodeKinds = ["Start", "Agent", "Tool", "Condition", "Parallel", "Join", "Pause", "End"] as const;
export type GraphWorkflowNodeKind = (typeof graphWorkflowNodeKinds)[number];

/** Every node carries a join policy; `All` is the parser's default. `Any` is what a reconverging End needs. */
export const graphWorkflowJoinPolicies = ["All", "Any"] as const;
export type GraphWorkflowJoinPolicy = (typeof graphWorkflowJoinPolicies)[number];

/** Run lifecycle. No `Paused`: a Graph Workflow parks on a Pause node as `WaitingForApproval`. */
export const graphWorkflowRunStatuses = [
	"Pending",
	"Running",
	"WaitingForApproval",
	"Cancelling",
	"Completed",
	"Failed",
	"Cancelled",
] as const;
export type GraphWorkflowRunStatus = (typeof graphWorkflowRunStatuses)[number];

/** Node-run lifecycle. Every node of the pinned graph is materialized at run start, so `Pending` is the common state. */
export const graphWorkflowNodeRunStatuses = [
	"Pending",
	"Queued",
	"Running",
	"WaitingForApproval",
	"Succeeded",
	"Failed",
	"Skipped",
	"Cancelled",
] as const;
export type GraphWorkflowNodeRunStatus = (typeof graphWorkflowNodeRunStatuses)[number];

/** What a Pause node's gate accepts. The server computes the offered set per node; never widen it client-side. */
export const graphWorkflowDecisionKinds = ["Approve", "Reject"] as const;
export type GraphWorkflowDecisionKind = (typeof graphWorkflowDecisionKinds)[number];

/** A real C# enum persisted as text, so it crosses the wire as a member name. `None` means "nothing went wrong". */
export const graphWorkflowFailureClasses = [
	"None",
	"NodeFailed",
	"Timeout",
	"AttemptsExhausted",
	"OutputTooLarge",
	"GateRejected",
	"ValidationFailed",
	"Cancelled",
	"Interrupted",
] as const;
export type GraphWorkflowFailureClass = (typeof graphWorkflowFailureClasses)[number];

/**
 * Edge condition operators. Canonical spelling is PascalCase and that is what the editor WRITES, but the server parses
 * them case-insensitively, so a graph stored by an older client (or hand-authored, as the S2 live graph was) may carry
 * `eq`. Reads go through `normalizeGraphWorkflowConditionOperator`.
 */
export const graphWorkflowConditionOperators = ["Eq", "Ne", "Gt", "Gte", "Lt", "Lte", "Exists", "NotExists"] as const;
export type GraphWorkflowConditionOperator = (typeof graphWorkflowConditionOperators)[number];

/** The nineteen event types the run trail can carry. Dotted tokens — see `graphWorkflowEventTypeLabelKey`. */
export const graphWorkflowEventTypes = [
	"run.created",
	"run.started",
	"run.waiting",
	"run.paused",
	"run.resumed",
	"run.completed",
	"run.failed",
	"run.cancelled",
	"node.materialized",
	"node.queued",
	"node.started",
	"node.completed",
	"node.failed",
	"node.retried",
	"node.skipped",
	"node.cancelled",
	"node.interrupted",
	"gate.requested",
	"gate.decided",
] as const;
export type GraphWorkflowEventType = (typeof graphWorkflowEventTypes)[number];

/**
 * The page's tab selection, carried in the `?tab=` search param. `editor` authors the definition, `runs` shows the
 * pinned graph and its node runs, `events` the run trail.
 */
export const graphWorkflowTabs = ["editor", "runs", "events"] as const;
export type GraphWorkflowTab = (typeof graphWorkflowTabs)[number];

/**
 * Everything about the page that must survive a reload and be shareable as a URL. It lives in search params, never in
 * the path: the route is a single file route with no `$param` segment.
 */
export interface GraphWorkflowSelection {
	readonly definitionId?: string;
	readonly runId?: string;
	readonly nodeKey?: string;
	readonly tab?: GraphWorkflowTab;
}

function narrow<T extends string>(values: readonly T[], value: string | undefined | null, fallback: T): T {
	return values.includes(value as T) ? (value as T) : fallback;
}

function asMember<T extends string>(values: readonly T[], value: string | undefined | null): T | undefined {
	return values.includes(value as T) ? (value as T) : undefined;
}

/** An unknown kind reads as `End`: the one shape with no outbound edges and no configuration to mis-offer. */
export function narrowGraphWorkflowNodeKind(value: string | undefined | null): GraphWorkflowNodeKind {
	return narrow(graphWorkflowNodeKinds, value, "End");
}

/** Absent means `All` — the parser's own default, so the editor and the runtime agree on an unset field. */
export function narrowGraphWorkflowJoinPolicy(value: string | undefined | null): GraphWorkflowJoinPolicy {
	return narrow(graphWorkflowJoinPolicies, value, "All");
}

/** An unknown run status reads as `Pending`: never started, no lifecycle command offered. */
export function narrowGraphWorkflowRunStatus(value: string | undefined | null): GraphWorkflowRunStatus {
	return narrow(graphWorkflowRunStatuses, value, "Pending");
}

/** An unknown node-run status reads as `Pending`: the state with no controls and no motion. */
export function narrowGraphWorkflowNodeRunStatus(value: string | undefined | null): GraphWorkflowNodeRunStatus {
	return narrow(graphWorkflowNodeRunStatuses, value, "Pending");
}

/** A failure always has to render SOMETHING, so this narrows rather than dropping (round-2 ruling C5). */
export function narrowGraphWorkflowFailureClass(value: string | undefined | null): GraphWorkflowFailureClass {
	return narrow(graphWorkflowFailureClasses, value, "NodeFailed");
}

/**
 * `undefined`, not a fallback: a decision kind drives a BUTTON. An unrecognised token rendered as `Approve` would
 * offer an operator a control that means something else.
 */
export function asGraphWorkflowDecisionKind(value: string | undefined | null): GraphWorkflowDecisionKind | undefined {
	return asMember(graphWorkflowDecisionKinds, value);
}

/** `allowedDecisions` filtered to members this client can render. Order follows the server's. */
export function toGraphWorkflowDecisionKinds(values: readonly string[] | undefined | null): readonly GraphWorkflowDecisionKind[] {
	return (values ?? []).flatMap((value) => {
		const kind = asGraphWorkflowDecisionKind(value);
		return kind ? [kind] : [];
	});
}

/** `undefined` for an unknown token: the trail shows the raw string rather than mislabelling it as another event. */
export function asGraphWorkflowEventType(value: string | undefined | null): GraphWorkflowEventType | undefined {
	return asMember(graphWorkflowEventTypes, value);
}

/**
 * Canonicalises a stored `op` token to its PascalCase member, case-insensitively — exactly as the server's own
 * `Enum.TryParse(..., ignoreCase: true)` does. `undefined` for anything else: an unrecognised operator has no safe
 * substitute, since guessing `Eq` would silently rewrite a branch on save.
 */
export function normalizeGraphWorkflowConditionOperator(
	value: string | undefined | null,
): GraphWorkflowConditionOperator | undefined {
	const lowered = (value ?? "").toLowerCase();
	return graphWorkflowConditionOperators.find((operator) => operator.toLowerCase() === lowered);
}

/**
 * i18next reads `.` as a key separator, so a dotted event token cannot be a leaf key: `run.created` would resolve as
 * `run` → `created`. The label keys therefore use `_` and this helper is the ONE place that mapping lives.
 */
export function graphWorkflowEventTypeLabelKey(eventType: string): string {
	return `pages.graphWorkflows.eventType.${eventType.replaceAll(".", "_")}`;
}

/** Nothing more happens on this run without a new one being started. */
export function isTerminalGraphWorkflowRunStatus(status: GraphWorkflowRunStatus): boolean {
	return status === "Completed" || status === "Failed" || status === "Cancelled";
}

/** The node run has stopped for good: the runtime has routed past it and its successors carry the outcome. */
export function isTerminalGraphWorkflowNodeRunStatus(status: GraphWorkflowNodeRunStatus): boolean {
	return status === "Succeeded" || status === "Failed" || status === "Skipped" || status === "Cancelled";
}

/** Mirrors `GraphWorkflowOptions.MaxNodesPerDefinition`: the editor refuses a save past this before the server does. */
export const GRAPH_WORKFLOW_MAX_NODES = 200;

/** Mirrors `GraphWorkflowOptions.MaxNodeRunsPerRun`: past this the run view draws a table instead of a canvas. */
export const GRAPH_WORKFLOW_MAX_RENDERED_NODES = 200;

/** Mirrors `GraphWorkflowOptions.MaxRunInputBytes`: the start dialog blocks a larger input rather than posting a 400. */
export const GRAPH_WORKFLOW_MAX_RUN_INPUT_BYTES = 65_536;

/** Node and edge keys share one namespace and this pattern, server-side and here. */
export const GRAPH_WORKFLOW_KEY_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;

/**
 * The `maxAttempts` a new node starts on (F-1 ruling): 3 for the two kinds that call something fallible, 1 for the
 * structural kinds, where a retry would re-evaluate the same inputs to the same answer.
 */
export function graphWorkflowDefaultMaxAttempts(kind: GraphWorkflowNodeKind): number {
	return kind === "Agent" || kind === "Tool" ? 3 : 1;
}
