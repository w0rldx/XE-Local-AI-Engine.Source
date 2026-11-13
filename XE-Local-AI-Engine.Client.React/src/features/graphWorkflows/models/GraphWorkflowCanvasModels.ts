// The graph editor's canvas types and the constants every canvas reader shares. Pure: no React, and only React Flow's
// `Node`/`Edge` TYPES. Why the JSON-shaped fields are strings: `GraphWorkflowCanvasConversion`.
//
// Node data is a DISCRIMINATED UNION on `kind`. With eight kinds and four of them carrying real config, the flat
// alternative — one interface holding every agent, tool, pause and condition field as optional — is an untyped
// grab-bag that no reader can narrow and no compiler can check a card against.
//
// `React Flow node id === node key` and `edge.id === edge.key`. One identifier addresses the card, the edge endpoint,
// the node run and the decision, so "which node did the run fail on" needs no lookup table. Node and edge keys share
// ONE namespace (wiki page 21 §2.1), which is what keeps a validation issue's `subject` unambiguous about what it points at.

import type { Edge, Node } from "@xyflow/react";

import type { ChatSamplingOptions } from "@/core/runtime/ChatSamplingOptions";
import type {
	GraphWorkflowConditionOperator,
	GraphWorkflowDecisionKind,
	GraphWorkflowDefinitionKind,
	GraphWorkflowFailureClass,
	GraphWorkflowJoinPolicy,
	GraphWorkflowNodeKind,
	GraphWorkflowNodeRunStatus,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

/** What a run adds to a card. Absent on the editor's canvas, where nothing has run. */
export interface GraphWorkflowCanvasRunState {
	readonly status: GraphWorkflowNodeRunStatus;
	readonly attempt: number;
	readonly failureClass: GraphWorkflowFailureClass;
	readonly pendingDecisionKind?: GraphWorkflowDecisionKind;
}

export interface GraphWorkflowNodeBase extends Record<string, unknown> {
	/** The author-stable node key. Also the React Flow node id — see the header. */
	readonly key: string;
	readonly label: string;
	/** A property of EVERY node, not just `Join` (wiki page 21 §2.3). `All` is the parser's default. */
	readonly joinPolicy: GraphWorkflowJoinPolicy;
	readonly maxAttempts?: number;
	readonly timeoutSeconds?: number;
	readonly runState?: GraphWorkflowCanvasRunState;
}

/** A Mantine list of rows needs stable order; the wire wants an object map. `canvasToGraph` converts. */
export interface GraphWorkflowArgumentBinding {
	readonly parameter: string;
	readonly path: string;
}

export type GraphWorkflowInputBinding = GraphWorkflowArgumentBinding;
export type GraphWorkflowSamplingOptions = Omit<ChatSamplingOptions, "seed"> & { seed?: string };

/**
 * The chat flags. `undefined` is "absent on the wire", which the parser reads as its own default (`false`, or `true`
 * for an End in a Chat graph) — held as absent rather than defaulted so a Standard graph saves byte for byte as it was.
 */
interface GraphWorkflowChatFlags {
	readonly publishToChat?: boolean;
}

export interface GraphWorkflowAttachmentFlags extends GraphWorkflowChatFlags {
	readonly includeAttachments?: boolean;
}

/** The graph-level `chat` block, member by member as stored: an absent member is the parser's default. */
export interface GraphWorkflowChatSettings {
	readonly acceptsAttachments?: boolean;
	readonly requireRerunConfirmation?: boolean;
}

/** What the graph says about itself, outside its nodes and edges. `chat` is only written for a Chat graph. */
export interface GraphWorkflowGraphSettings {
	readonly kind: GraphWorkflowDefinitionKind;
	readonly chat?: GraphWorkflowChatSettings;
}

export const standardGraphSettings: GraphWorkflowGraphSettings = { kind: "Standard" };

/** The parser's defaults for an absent `chat` member. */
export const graphWorkflowChatDefaults = { acceptsAttachments: false, requireRerunConfirmation: true } as const;

export type GraphWorkflowCanvasNodeData =
	| (GraphWorkflowNodeBase & {
			readonly kind: "Start";
			readonly inputSchema: string | null;
			readonly defaultInput: string | null;
			/** The phrases the intent router matches this workflow on (authoring metadata, never authority). */
			readonly aliases: readonly string[];
	  })
	| (GraphWorkflowNodeBase & {
			readonly kind: "LlmCall";
			readonly model: string | null;
			readonly systemPrompt: string | null;
			readonly prompt: string;
			readonly inputBindings: readonly GraphWorkflowInputBinding[];
			readonly reasoningEffort: string | null;
			readonly responseJsonSchema: string | null;
			readonly samplingOptions: GraphWorkflowSamplingOptions;
	  } & GraphWorkflowAttachmentFlags)
	| (GraphWorkflowNodeBase & {
			readonly kind: "Agent";
			readonly agentDefinitionId: string | null;
			readonly instructions: string;
			readonly model: string | null;
			readonly reasoningEffort: string | null;
			readonly responseJsonSchema: string | null;
			readonly includeUpstreamOutputs: boolean;
			/** Reads the run's Development project; absent on the wire is `false`, held as absent like the chat flags. */
			readonly repository?: boolean;
	  } & GraphWorkflowAttachmentFlags)
	| (GraphWorkflowNodeBase & {
			readonly kind: "Tool";
			readonly toolName: string | null;
			readonly argumentsJson: string;
			readonly argumentBindings: readonly GraphWorkflowArgumentBinding[];
			/** web_fetch's link allow-list (ADR 0017); written to the wire only when non-empty. */
			readonly allowedUrls: readonly string[];
	  })
	| (GraphWorkflowNodeBase & { readonly kind: "Condition"; readonly path: string | null })
	| (GraphWorkflowNodeBase & { readonly kind: "Parallel" | "Join" })
	| (GraphWorkflowNodeBase & {
			readonly kind: "Pause";
			readonly prompt: string;
			readonly allowedDecisions: readonly GraphWorkflowDecisionKind[];
			readonly requireComment: boolean;
	  })
	| (GraphWorkflowNodeBase & { readonly kind: "ChatInput"; readonly prompt: string })
	| (GraphWorkflowNodeBase & {
			readonly kind: "DecisionModel";
			readonly question: string;
			/** Absent means `choice`. A string, not the union, so an imported unknown value stays visible. */
			readonly questionType: string | null;
			/** Choice labels, or score levels lowest first. Kept on the canvas for `noul` but not sent. */
			readonly labels: readonly string[];
			/** Choice only: label → description. Not sent for `noul` or `score`. */
			readonly criteria: Readonly<Record<string, string>>;
			readonly provider: string | null;
			readonly model: string | null;
			readonly inputBindings: readonly GraphWorkflowInputBinding[];
	  })
	| (GraphWorkflowNodeBase & {
			readonly kind: "DevTask";
			/** `implement` or `apply`. A string, not the union, so an imported unknown value stays visible. */
			readonly action: string;
			/** The members below `action` are `implement`'s, except `task`, which only `apply` sends. Both stay on the canvas. */
			readonly title: string;
			readonly requirements: string;
			readonly acceptanceCriteria: readonly string[];
			readonly instructions: string;
			readonly inputBindings: readonly GraphWorkflowInputBinding[];
			/** The key of the `implement` node an `apply` node lands (DEVTASK-1). */
			readonly task: string | null;
	  })
	| (GraphWorkflowNodeBase & {
			readonly kind: "End";
			readonly outcome: string;
			readonly resultPath: string | null;
	  } & GraphWorkflowChatFlags);

export interface GraphWorkflowCanvasEdgeCondition {
	readonly path?: string;
	readonly op: GraphWorkflowConditionOperator;
	/** A STRING on the canvas, for the same reason the JSON fields are: it is parsed once, at `canvasToGraph`. */
	readonly value: string;
}

export interface GraphWorkflowEdgeData extends Record<string, unknown> {
	readonly label?: string;
	readonly condition?: GraphWorkflowCanvasEdgeCondition;
}

export type GraphWorkflowCanvasNode = Node<GraphWorkflowCanvasNodeData>;
export type GraphWorkflowCanvasEdge = Edge<GraphWorkflowEdgeData>;

export interface GraphWorkflowCanvas {
	readonly nodes: GraphWorkflowCanvasNode[];
	readonly edges: GraphWorkflowCanvasEdge[];
}

/** A canvas read off a wire graph, with the graph-level settings that are not nodes or edges. */
export interface GraphWorkflowLoadedCanvas extends GraphWorkflowCanvas {
	readonly settings: GraphWorkflowGraphSettings;
}

/** React Flow type keys — the keys the canvas registers in its `nodeTypes` map, one component per kind. */
export const graphWorkflowNodeTypeByKind: Record<GraphWorkflowNodeKind, string> = {
	Start: "start",
	Agent: "agent",
	LlmCall: "llm-call",
	Tool: "tool",
	Condition: "condition",
	Parallel: "parallel",
	Join: "join",
	Pause: "pause",
	End: "end",
	ChatInput: "chat-input",
	DecisionModel: "decision-model",
	DevTask: "dev-task",
};
