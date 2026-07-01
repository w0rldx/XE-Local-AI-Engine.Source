import { z } from "zod";

// Open Canvas (Preview) workflow graph contract. Field names mirror the backend
// `PreviewWorkflowGraph.cs` (the ONE wire shape — stored blob ↔ this model ↔ runner DTO) EXACTLY:
// StartText, Nodes[], Edges[] with the documented casing. The wire serializes these as camelCase
// (the backend's default System.Text.Json policy), so the zod schemas below use camelCase property
// names that map 1:1 onto the C# PascalCase record members.

// Block kinds the canvas supports — mirrors PreviewWorkflowNodeKind (Start/Agent/Debug/Pause/End),
// which serializes as its string name via JsonStringEnumConverter.
export type PreviewNodeKind = "Start" | "Agent" | "Debug" | "Pause" | "End";

const previewNodeKindSchema = z.enum(["Start", "Agent", "Debug", "Pause", "End"]);

// One graph node. Only Agent nodes populate the agent fields (label/instructions/model/modelProfile/
// reasoningEffort); the others need only `id` (Start seed text lives on the graph, not the node).
// Modeled as one flat object (not a discriminated union) so it mirrors the single C# record shape.
const previewWorkflowGraphNodeSchema = z.object({
	id: z.string(),
	kind: previewNodeKindSchema,
	label: z.string().nullish(),
	instructions: z.string().nullish(),
	model: z.string().nullish(),
	modelProfile: z.string().nullish(),
	reasoningEffort: z.string().nullish(),
});

export type PreviewWorkflowGraphNode = z.infer<typeof previewWorkflowGraphNodeSchema>;

// A directed edge — mirrors PreviewWorkflowGraphEdge { SourceId, TargetId }.
const previewWorkflowGraphEdgeSchema = z.object({
	sourceId: z.string(),
	targetId: z.string(),
});

export type PreviewWorkflowGraphEdge = z.infer<typeof previewWorkflowGraphEdgeSchema>;

// The full graph — mirrors PreviewWorkflowGraph { StartText, Nodes, Edges }.
const previewWorkflowGraphSchema = z.object({
	startText: z.string(),
	nodes: z.array(previewWorkflowGraphNodeSchema),
	edges: z.array(previewWorkflowGraphEdgeSchema),
});

export type PreviewWorkflowGraph = z.infer<typeof previewWorkflowGraphSchema>;

// List-row projection (no graph) — mirrors PreviewWorkflowSummaryResponse(Id, Name, Version,
// CreatedAtUtc, UpdatedAtUtc). Timestamps are epoch milliseconds (long on the wire).
const previewWorkflowSummarySchema = z.object({
	id: z.string(),
	name: z.string(),
	version: z.number(),
	createdAtUtc: z.number(),
	updatedAtUtc: z.number(),
});

export type PreviewWorkflowSummary = z.infer<typeof previewWorkflowSummarySchema>;

// List response — mirrors ListPreviewWorkflowsResponse { Items }.
export const listPreviewWorkflowsResponseSchema = z.object({
	items: z.array(previewWorkflowSummarySchema),
});

// Full workflow including the deserialized graph — mirrors PreviewWorkflowResponse(Id, Name, Graph,
// Version, CreatedAtUtc, UpdatedAtUtc).
export const previewWorkflowDetailSchema = z.object({
	id: z.string(),
	name: z.string(),
	graph: previewWorkflowGraphSchema,
	version: z.number(),
	createdAtUtc: z.number(),
	updatedAtUtc: z.number(),
});

export type PreviewWorkflowDetail = z.infer<typeof previewWorkflowDetailSchema>;

// Execute response — the new run id the client subscribes to over the hub. Mirrors
// PreviewRunStartedResponse(RunId).
export const previewRunStartedResponseSchema = z.object({
	runId: z.string(),
});

export type PreviewRunStartedResponse = z.infer<typeof previewRunStartedResponseSchema>;

// --- SignalR run-update event payloads ---
// EVERY event carries `runId` (Guid on the wire → string here) — that is the cross-run contamination
// guard (a single hub connection may drive several runs). Payloads are untrusted wire data, so every
// field is narrowed defensively. The wire shape mirrors PreviewWorkflowNodeHubEvent /
// PreviewWorkflowRunHubEvent (camelCase). `eventType` doubles as the wire discriminator and equals the
// SignalR method name (see previewHubEvents below).

// Node-scoped event (preview.node.started|output|debug|completed|failed). `output` carries the
// operator's transient node/debug output; `error` a sanitized failure message. `seq` is a per-run
// monotonically increasing sequence number (shared with run events of the same run) — the client uses
// it to dedupe a replayed event (backend buffers + replays on Subscribe) against the same event arriving
// live, so a late-subscribing connection never applies an event twice.
export const previewNodeEventSchema = z.object({
	eventType: z.string(),
	runId: z.string(),
	nodeId: z.string(),
	output: z.string().nullish(),
	error: z.string().nullish(),
	occurredAtUtc: z.number(),
	seq: z.number(),
});

export type PreviewNodeEvent = z.infer<typeof previewNodeEventSchema>;

// Run-lifecycle event (preview.run.started|paused|completed|failed|cancelled). `nodeId` is set only for
// pause (the Pause node); `output` for pause (upstream display) and completed (terminal output);
// `requestId` for pause (the resume token); `error` for failed. `seq` — see previewNodeEventSchema.
export const previewRunEventSchema = z.object({
	eventType: z.string(),
	runId: z.string(),
	nodeId: z.string().nullish(),
	output: z.string().nullish(),
	error: z.string().nullish(),
	requestId: z.string().nullish(),
	occurredAtUtc: z.number(),
	seq: z.number(),
});

export type PreviewRunEvent = z.infer<typeof previewRunEventSchema>;

// Stable SignalR client-method names for preview events — mirrors PreviewWorkflowHubEvents (C#). These
// double as the wire `eventType` discriminator, so the client subscribes per event name.
export const previewHubEvents = {
	nodeStarted: "preview.node.started",
	nodeOutput: "preview.node.output",
	nodeDebug: "preview.node.debug",
	nodeCompleted: "preview.node.completed",
	nodeFailed: "preview.node.failed",
	runStarted: "preview.run.started",
	runPaused: "preview.run.paused",
	runCompleted: "preview.run.completed",
	runFailed: "preview.run.failed",
	runCancelled: "preview.run.cancelled",
} as const;

export type PreviewNodeEventName =
	| typeof previewHubEvents.nodeStarted
	| typeof previewHubEvents.nodeOutput
	| typeof previewHubEvents.nodeDebug
	| typeof previewHubEvents.nodeCompleted
	| typeof previewHubEvents.nodeFailed;

export type PreviewRunEventName =
	| typeof previewHubEvents.runStarted
	| typeof previewHubEvents.runPaused
	| typeof previewHubEvents.runCompleted
	| typeof previewHubEvents.runFailed
	| typeof previewHubEvents.runCancelled;

export const previewNodeEventNames: readonly PreviewNodeEventName[] = [
	previewHubEvents.nodeStarted,
	previewHubEvents.nodeOutput,
	previewHubEvents.nodeDebug,
	previewHubEvents.nodeCompleted,
	previewHubEvents.nodeFailed,
];

export const previewRunEventNames: readonly PreviewRunEventName[] = [
	previewHubEvents.runStarted,
	previewHubEvents.runPaused,
	previewHubEvents.runCompleted,
	previewHubEvents.runFailed,
	previewHubEvents.runCancelled,
];

// True while a run can still be cancelled/continued (it has not reached a terminal lifecycle event).
const TERMINAL_RUN_EVENTS: readonly string[] = [
	previewHubEvents.runCompleted,
	previewHubEvents.runFailed,
	previewHubEvents.runCancelled,
];

export function isTerminalRunEvent(eventType: string): boolean {
	return TERMINAL_RUN_EVENTS.includes(eventType);
}
