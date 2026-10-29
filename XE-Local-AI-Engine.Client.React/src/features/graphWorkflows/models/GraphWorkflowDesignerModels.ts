// The workflow designer: one assistant turn over the graph open on the canvas, and what the canvas draws while the
// operator reviews the graph the model proposed. Pure data and pure functions; the turn call lives in the queries file
// and the review state in `useGraphWorkflowEditor`.

import {
	type GraphWorkflowCanvasEdge,
	type GraphWorkflowCanvasNode,
	type GraphWorkflowGraphSettings,
	graphToCanvas,
	graphWorkflowChatDefaults,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import type {
	GraphWorkflowDesignerChangeResponse,
	GraphWorkflowGraph,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

/** One message of the conversation the client holds and sends whole with every turn. */
export interface GraphWorkflowDesignerMessage {
	readonly role: "user" | "assistant";
	readonly text: string;
}

/**
 * What a canvas element is in a proposal, relative to the working graph: `added`, `modified` or `removed`, the wire's
 * own tokens. Absent means unchanged.
 */
export type GraphWorkflowDiff = string;

export interface GraphWorkflowDesignerChangeSummary {
	readonly added: number;
	readonly modified: number;
	readonly removed: number;
}

/** Node and edge counts only: the graph-level settings entry (`kind: "graph"`) is named by {@link designerSettingsDiff}. */
export function summarizeDesignerChanges(
	changes: readonly GraphWorkflowDesignerChangeResponse[],
): GraphWorkflowDesignerChangeSummary {
	const elements = changes.filter((entry) => entry.kind === "node" || entry.kind === "edge");
	return {
		added: elements.filter((entry) => entry.change === "added").length,
		modified: elements.filter((entry) => entry.change === "modified").length,
		removed: elements.filter((entry) => entry.change === "removed").length,
	};
}

export type GraphWorkflowSettingsDiffEntry =
	| { readonly member: "kind"; readonly from: string; readonly to: string }
	| { readonly member: "acceptsAttachments" | "requireRerunConfirmation"; readonly from: boolean; readonly to: boolean };

/**
 * The graph-level settings a proposal changes, member by member, compared at their effective values (an absent chat
 * member is its parser default). The chat members only matter on a Chat graph, so they are compared only when either
 * side is one.
 */
export function designerSettingsDiff(
	working: GraphWorkflowGraphSettings,
	proposed: GraphWorkflowGraphSettings,
): readonly GraphWorkflowSettingsDiffEntry[] {
	const entries: GraphWorkflowSettingsDiffEntry[] = [];
	if (working.kind !== proposed.kind) {
		entries.push({ member: "kind", from: working.kind, to: proposed.kind });
	}
	if (working.kind === "Chat" || proposed.kind === "Chat") {
		for (const member of ["acceptsAttachments", "requireRerunConfirmation"] as const) {
			const from = working.chat?.[member] ?? graphWorkflowChatDefaults[member];
			const to = proposed.chat?.[member] ?? graphWorkflowChatDefaults[member];
			if (from !== to) {
				entries.push({ member, from, to });
			}
		}
	}
	return entries;
}

/**
 * The proposal as React Flow elements, each tagged with its `diff` from the server's change list. A removed node or
 * edge is not in the proposal, so it is taken from the WORKING canvas and drawn as a ghost beside what replaces it.
 *
 * The server computes `changes` on the parsed graphs, so the client never re-derives a diff: a member written out at its
 * parser default would otherwise read as a change.
 */
export function designerProposalCanvas(
	working: { readonly nodes: readonly GraphWorkflowCanvasNode[]; readonly edges: readonly GraphWorkflowCanvasEdge[] },
	proposal: GraphWorkflowGraph,
	changes: readonly GraphWorkflowDesignerChangeResponse[],
): {
	readonly nodes: readonly GraphWorkflowCanvasNode[];
	readonly edges: readonly GraphWorkflowCanvasEdge[];
	/** The proposal's graph-level settings, for the read-only settings view during review. */
	readonly settings: GraphWorkflowGraphSettings;
} {
	const diffOf = (kind: "node" | "edge", key: string): GraphWorkflowDiff | undefined =>
		changes.find((entry) => entry.kind === kind && entry.key === key)?.change;
	const canvas = graphToCanvas(proposal);
	const removed = (kind: "node" | "edge", key: string): boolean => diffOf(kind, key) === "removed";
	return {
		settings: canvas.settings,
		nodes: [
			...canvas.nodes.map((node) => withNodeDiff(node, diffOf("node", node.id))),
			...working.nodes.filter((node) => removed("node", node.id)).map((node) => withNodeDiff(node, "removed")),
		],
		edges: [
			...canvas.edges.map((edge) => withEdgeDiff(edge, diffOf("edge", edge.id))),
			...working.edges.filter((edge) => removed("edge", edge.id)).map((edge) => withEdgeDiff(edge, "removed")),
		],
	};
}

function withNodeDiff(node: GraphWorkflowCanvasNode, diff: GraphWorkflowDiff | undefined): GraphWorkflowCanvasNode {
	return diff === undefined ? node : { ...node, data: { ...node.data, diff } };
}

function withEdgeDiff(edge: GraphWorkflowCanvasEdge, diff: GraphWorkflowDiff | undefined): GraphWorkflowCanvasEdge {
	return diff === undefined ? edge : { ...edge, data: { ...edge.data, diff } };
}
