// Pure derivations behind the chat's workflow surfaces: the taken path the status card draws and the rows the activity
// block lists. Everything here reads the run's PINNED graph (off `GET runs/{runId}`) and its node runs; nothing ticks.
//
// The ranks come from the Graph Workflows layout helper rather than a second walk of the graph, so the chat and the run
// view can never disagree on which nodes sit side by side (parallel branches share a rank).

import { layoutGraphWorkflow } from "@/features/graphWorkflows/models/GraphWorkflowLayout";
import {
	type GraphWorkflowGraph,
	type GraphWorkflowNodeRunResponse,
	type GraphWorkflowNodeRunStatus,
	type GraphWorkflowNodeRunSummaryResponse,
	type GraphWorkflowRunEventResponse,
	type GraphWorkflowRunStatus,
	isTerminalGraphWorkflowRunStatus,
	narrowGraphWorkflowNodeRunStatus,
	narrowGraphWorkflowRunStatus,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

export interface WorkflowPathNode {
	readonly key: string;
	readonly label: string;
	readonly kind: string;
	readonly status: GraphWorkflowNodeRunStatus;
	readonly startedAtUtc?: number | null;
	readonly completedAtUtc?: number | null;
	/**
	 * For a node that calls a model (Agent, LLM Call, DecisionModel): the pinned config's `model`, `null` when it runs
	 * on the default. Absent for every other kind.
	 */
	readonly model?: string | null;
}

const modelNodeKinds: ReadonlySet<string> = new Set(["Agent", "LlmCall", "DecisionModel"]);

function modelOf(node: GraphNodeShape): { model?: string | null } {
	if (!modelNodeKinds.has(node.kind ?? "")) {
		return {};
	}
	const model = (node.config as { model?: unknown } | null | undefined)?.model;
	return { model: typeof model === "string" && model.trim().length > 0 ? model : null };
}

/** One entry per graph rank, in rank order; the nodes inside a rank are the parallel branches. */
export type WorkflowPath = readonly (readonly WorkflowPathNode[])[];

interface GraphNodeShape {
	readonly key?: string;
	readonly kind?: string;
	readonly label?: string | null;
	readonly config?: unknown;
}

function graphNodes(graph: GraphWorkflowGraph | undefined): readonly GraphNodeShape[] {
	return graph?.nodes ?? [];
}

/** The node's label, falling back to its key: an unlabelled node is still named by something the operator typed. */
export function workflowNodeLabel(graph: GraphWorkflowGraph | undefined, nodeKey: string): string {
	const label = graphNodes(graph)
		.find((node) => node.key === nodeKey)
		?.label?.trim();
	return label && label.length > 0 ? label : nodeKey;
}

/**
 * The run's nodes grouped by layout rank. The node runs are the truth for state; the graph supplies order, labels and
 * which nodes are siblings. A node run whose key is not in the graph is still listed (in its own trailing rank) —
 * hiding one would understate what happened.
 */
export function toWorkflowPath(
	graph: GraphWorkflowGraph | undefined,
	nodeRuns: readonly GraphWorkflowNodeRunSummaryResponse[],
): WorkflowPath {
	const runByKey = new Map(nodeRuns.map((nodeRun) => [nodeRun.nodeKey ?? "", nodeRun]));
	const nodes = graphNodes(graph).filter((node): node is GraphNodeShape & { key: string } => Boolean(node.key));
	const layout = layoutGraphWorkflow(
		nodes.map((node) => ({ key: node.key })),
		(graph?.edges ?? []).map((edge) => ({ from: edge.from ?? "", to: edge.to ?? "" })),
	);
	const ranks: WorkflowPathNode[][] = Array.from({ length: layout.rankCount }, () => []);
	const placed = new Set<string>();
	for (const node of nodes.toSorted(
		(left, right) => (layout.positions.get(left.key)?.indexInRank ?? 0) - (layout.positions.get(right.key)?.indexInRank ?? 0),
	)) {
		const nodeRun = runByKey.get(node.key);
		ranks[layout.positions.get(node.key)?.rank ?? 0]?.push({
			key: node.key,
			label: workflowNodeLabel(graph, node.key),
			kind: node.kind ?? nodeRun?.kind ?? "",
			status: narrowGraphWorkflowNodeRunStatus(nodeRun?.status),
			startedAtUtc: nodeRun?.startedAtUtc,
			completedAtUtc: nodeRun?.completedAtUtc,
			...modelOf(node),
		});
		placed.add(node.key);
	}
	const orphans = nodeRuns
		.filter((nodeRun) => !placed.has(nodeRun.nodeKey ?? ""))
		.map((nodeRun) => ({
			key: nodeRun.nodeKey ?? "",
			label: nodeRun.nodeKey ?? "",
			kind: nodeRun.kind ?? "",
			status: narrowGraphWorkflowNodeRunStatus(nodeRun.status),
			startedAtUtc: nodeRun.startedAtUtc,
			completedAtUtc: nodeRun.completedAtUtc,
		}));
	return [...ranks.filter((rank) => rank.length > 0), ...(orphans.length > 0 ? [orphans] : [])];
}

/** ✓ succeeded · ● running/queued/waiting · ○ pending · ✕ failed or cancelled. Skipped keeps ○ and renders dimmed. */
export function workflowNodeGlyph(status: GraphWorkflowNodeRunStatus): string {
	switch (status) {
		case "Succeeded":
			return "✓";
		case "Running":
		case "Queued":
		case "WaitingForApproval":
			return "●";
		case "Failed":
		case "Cancelled":
			return "✕";
		default:
			return "○";
	}
}

const activeStatusOrder: readonly GraphWorkflowNodeRunStatus[] = ["Running", "Queued", "WaitingForApproval"];

/** The node the run is on right now: a running one first, then a queued one, then one parked on a person. */
export function activeWorkflowNode(path: WorkflowPath): WorkflowPathNode | undefined {
	const flat = path.flat();
	for (const status of activeStatusOrder) {
		const node = flat.find((candidate) => candidate.status === status);
		if (node) {
			return node;
		}
	}
	return undefined;
}

/** The run is parked on a ChatInput (a question for the user), not on a Pause. */
export function isWaitingForWorkflowInput(path: WorkflowPath): boolean {
	return path.flat().some((node) => node.kind === "ChatInput" && node.status === "WaitingForApproval");
}

/**
 * A node run's `error` column also carries the lane's queue reason while the row waits for a slot
 * (`awaiting-invocation-slot`, `awaiting-agent-slot`), and the token survives the row succeeding. So only a FAILED
 * node's error is a reason worth showing, and never one of those phase tokens.
 */
export function workflowNodeFailureReason(
	status: string | undefined | null,
	error: string | null | undefined,
): string | undefined {
	if (narrowGraphWorkflowNodeRunStatus(status) !== "Failed" || !error || /^awaiting-[a-z-]+$/.test(error)) {
		return undefined;
	}
	return error;
}

/** The first node (in graph order) that ended in `status` — where a failed or cancelled run stopped. */
export function workflowNodeWithStatus(path: WorkflowPath, status: GraphWorkflowNodeRunStatus): WorkflowPathNode | undefined {
	return path.flat().find((node) => node.status === status);
}

/** A run still doing (or waiting to do) work. `WaitingForApproval` counts: a parked run is live until answered. */
export function isLiveWorkflowRun(status: string | undefined | null): boolean {
	return !isTerminalGraphWorkflowRunStatus(narrowGraphWorkflowRunStatus(status));
}

/** The statuses in which a chat send would be refused as busy (everything live except a ChatInput park). */
export function isBusyWorkflowRun(status: GraphWorkflowRunStatus, hasPendingInput: boolean): boolean {
	return !isTerminalGraphWorkflowRunStatus(status) && !(status === "WaitingForApproval" && hasPendingInput);
}

/** `42s`, `3m 07s`, `1h 02m`. Negative spans (clock skew between node and browser) read as zero. */
export function formatWorkflowElapsed(milliseconds: number): string {
	const totalSeconds = Math.max(0, Math.floor(milliseconds / 1000));
	const hours = Math.floor(totalSeconds / 3600);
	const minutes = Math.floor((totalSeconds % 3600) / 60);
	const seconds = totalSeconds % 60;
	if (hours > 0) {
		return `${hours}h ${minutes.toString().padStart(2, "0")}m`;
	}
	if (minutes > 0) {
		return `${minutes}m ${seconds.toString().padStart(2, "0")}s`;
	}
	return `${seconds}s`;
}

function at(value: unknown, ...path: readonly string[]): unknown {
	let current = value;
	for (const segment of path) {
		if (current === null || typeof current !== "object" || Array.isArray(current)) {
			return undefined;
		}
		current = (current as Record<string, unknown>)[segment];
	}
	return current;
}

function textAt(value: unknown, ...path: readonly string[]): string | undefined {
	const found = at(value, ...path);
	return typeof found === "string" && found.trim().length > 0 ? found : undefined;
}

const TOOL_SUMMARY_MAX_CHARS = 160;

function summarize(value: unknown): string | undefined {
	if (value === undefined || value === null) {
		return undefined;
	}
	const text = typeof value === "string" ? value : JSON.stringify(value);
	const flat = text.replace(/\s+/g, " ").trim();
	if (flat.length === 0) {
		return undefined;
	}
	return flat.length > TOOL_SUMMARY_MAX_CHARS ? `${flat.slice(0, TOOL_SUMMARY_MAX_CHARS - 1)}…` : flat;
}

/** One row of the activity block. Every optional member is present only when the node produced it. */
export interface WorkflowActivityEntry {
	readonly key: string;
	readonly label: string;
	readonly kind: string;
	readonly status: GraphWorkflowNodeRunStatus;
	readonly durationMs?: number;
	/** Which way the node routed: a DecisionModel's `choice`, otherwise the label of the conditional edge that fired. */
	readonly route?: string;
	/** An Agent / LLM Call node's text output — intermediate work, shown behind a disclosure. */
	readonly text?: string;
	/** A Tool node's name and a one-line summary of its result. */
	readonly tool?: { readonly name: string; readonly summary?: string };
	/** A ChatInput's question and the user's answer (absent while it is still waiting). */
	readonly input?: { readonly prompt: string; readonly answer?: string };
	readonly published: boolean;
	readonly error?: string;
}

/** The node kinds whose detail document the activity block reads (the rest are fully described by their summary row). */
export const workflowActivityDetailKinds: ReadonlySet<string> = new Set([
	"Agent",
	"LlmCall",
	"DecisionModel",
	"Condition",
	"Tool",
	"ChatInput",
]);

/**
 * The activity rows for one run, in graph order. Nodes that never ran (`Pending`) are left out — the status card
 * already shows what is still ahead; a skipped node stays in, because "this branch was not taken" is routing.
 */
export function toWorkflowActivity(
	graph: GraphWorkflowGraph | undefined,
	path: WorkflowPath,
	detailsByKey: ReadonlyMap<string, GraphWorkflowNodeRunResponse>,
	events: readonly GraphWorkflowRunEventResponse[],
): readonly WorkflowActivityEntry[] {
	const published = new Set(events.filter((event) => event.eventType === "node.published").map((event) => event.nodeKey ?? ""));
	const configByKey = new Map(graphNodes(graph).map((node) => [node.key ?? "", node.config]));
	return path
		.flat()
		.filter((node) => node.status !== "Pending")
		.map((node): WorkflowActivityEntry => {
			const detail = detailsByKey.get(node.key);
			const envelope = detail?.output;
			const config = configByKey.get(node.key);
			const durationMs =
				typeof node.startedAtUtc === "number" && typeof node.completedAtUtc === "number"
					? node.completedAtUtc - node.startedAtUtc
					: undefined;
			const route = node.kind === "DecisionModel" ? textAt(envelope, "output", "choice") : textAt(envelope, "branch");
			const text = node.kind === "Agent" || node.kind === "LlmCall" ? textAt(envelope, "output", "text") : undefined;
			const toolName = node.kind === "Tool" ? textAt(config, "toolName") : undefined;
			const prompt = node.kind === "ChatInput" ? textAt(config, "prompt") : undefined;
			const error = workflowNodeFailureReason(node.status, detail?.error);
			return {
				key: node.key,
				label: node.label,
				kind: node.kind,
				status: node.status,
				published: published.has(node.key),
				...(durationMs !== undefined ? { durationMs } : {}),
				...(route ? { route } : {}),
				...(text ? { text } : {}),
				...(toolName ? { tool: { name: toolName, summary: summarize(at(envelope, "output", "result")) } } : {}),
				...(prompt ? { input: { prompt, answer: textAt(envelope, "output", "text") } } : {}),
				...(error ? { error } : {}),
			};
		});
}

/** `chat.acceptsAttachments` off a graph document; the parser's default is off. */
export function graphAcceptsAttachments(graph: GraphWorkflowGraph | undefined): boolean {
	return at(graph?.chat, "acceptsAttachments") === true;
}
