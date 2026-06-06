import { create } from "zustand";

import {
	isTerminalRunEvent,
	type PreviewNodeEvent,
	previewHubEvents,
	type PreviewRunEvent,
} from "@/features/preview/models/PreviewWorkflowModels";

// Transient run-output store for the Open Canvas (Preview) surface. State is keyed by `(runId, nodeId)` so two
// runs in flight on the same hub connection never cross-contaminate (decision #3 + MEDIUM-2): each Preview tab
// registers ONLY the runIds it started (registerRun) and the store IGNORES every hub event whose runId is not
// registered. On unmount the page calls `reset()` — so on mount/reload the store is EMPTY (no run output ever
// survives a navigation; nothing is persisted). The live output here is the operator's OWN transient run output
// (the Debug feature's whole point), never logged or indexed.

// Per-node live state for one run. `output` is the latest accumulated text from the node's started/output events;
// `debugOutput` is the raw upstream output a Debug node shows; `status` tracks the node lifecycle for styling.
export type PreviewNodeStatus = "idle" | "running" | "completed" | "failed";

export interface PreviewNodeRunState {
	readonly status: PreviewNodeStatus;
	readonly output: string;
	readonly debugOutput: string | null;
	readonly error: string | null;
}

// Lifecycle of one run as seen by this tab.
export type PreviewRunStatus = "running" | "paused" | "completed" | "failed" | "cancelled";

export interface PreviewRunState {
	readonly status: PreviewRunStatus;
	// Set while paused: the Pause node id + the resume request token (continue is run-scoped, but these surface
	// the upstream output and which node paused).
	readonly pausedNodeId: string | null;
	readonly pauseRequestId: string | null;
	readonly pauseOutput: string | null;
	// Terminal output (completed) or sanitized failure reason (failed), surfaced in the run toolbar.
	readonly finalOutput: string | null;
	readonly error: string | null;
	// Node states keyed by nodeId for this run only.
	readonly nodes: Readonly<Record<string, PreviewNodeRunState>>;
}

interface PreviewRunStore {
	// All runs this tab is tracking, keyed by runId. A run appears here only after registerRun; a hub event for an
	// unregistered runId is dropped (foreign-run guard).
	readonly runs: Readonly<Record<string, PreviewRunState>>;
	readonly actions: {
		// Register a runId this tab started (Execute). Idempotent — re-registering keeps existing state.
		registerRun: (runId: string) => void;
		applyNodeEvent: (event: PreviewNodeEvent) => void;
		applyRunEvent: (event: PreviewRunEvent) => void;
		// Clear ALL run state (called on page unmount so a reload starts empty).
		reset: () => void;
	};
}

const EMPTY_NODE_STATE: PreviewNodeRunState = { status: "idle", output: "", debugOutput: null, error: null };

function emptyRunState(): PreviewRunState {
	return { status: "running", pausedNodeId: null, pauseRequestId: null, pauseOutput: null, finalOutput: null, error: null, nodes: {} };
}

function updateNode(
	run: PreviewRunState,
	nodeId: string,
	patch: Partial<PreviewNodeRunState>,
): PreviewRunState {
	const current = run.nodes[nodeId] ?? EMPTY_NODE_STATE;
	return { ...run, nodes: { ...run.nodes, [nodeId]: { ...current, ...patch } } };
}

export const usePreviewRunStore = create<PreviewRunStore>()((set) => ({
	runs: {},
	actions: {
		registerRun: (runId) =>
			set((state) => (state.runs[runId] ? state : { runs: { ...state.runs, [runId]: emptyRunState() } })),

		applyNodeEvent: (event) =>
			set((state) => {
				// Foreign-run guard: drop events for runs this tab did not start.
				const run = state.runs[event.runId];
				if (run === undefined) {
					return state;
				}

				let next = run;
				switch (event.eventType) {
					case previewHubEvents.nodeStarted:
						next = updateNode(run, event.nodeId, { status: "running", error: null });
						break;
					case previewHubEvents.nodeOutput:
						// Accumulate streamed output text.
						next = updateNode(run, event.nodeId, {
							status: "running",
							output: (run.nodes[event.nodeId]?.output ?? "") + (event.output ?? ""),
						});
						break;
					case previewHubEvents.nodeDebug:
						// The raw output of the immediately-upstream node, shown live on the Debug node.
						next = updateNode(run, event.nodeId, { debugOutput: event.output ?? "" });
						break;
					case previewHubEvents.nodeCompleted:
						next = updateNode(run, event.nodeId, { status: "completed" });
						break;
					case previewHubEvents.nodeFailed:
						next = updateNode(run, event.nodeId, { status: "failed", error: event.error ?? null });
						break;
					default:
						return state;
				}

				return { runs: { ...state.runs, [event.runId]: next } };
			}),

		applyRunEvent: (event) =>
			set((state) => {
				const run = state.runs[event.runId];
				if (run === undefined) {
					return state;
				}

				let next = run;
				switch (event.eventType) {
					case previewHubEvents.runStarted:
						next = { ...run, status: "running" };
						break;
					case previewHubEvents.runPaused:
						next = {
							...run,
							status: "paused",
							pausedNodeId: event.nodeId ?? null,
							pauseRequestId: event.requestId ?? null,
							pauseOutput: event.output ?? null,
						};
						break;
					case previewHubEvents.runCompleted:
						next = { ...run, status: "completed", finalOutput: event.output ?? null };
						break;
					case previewHubEvents.runFailed:
						next = { ...run, status: "failed", error: event.error ?? null };
						break;
					case previewHubEvents.runCancelled:
						next = { ...run, status: "cancelled" };
						break;
					default:
						return state;
				}

				// On any terminal lifecycle event the pause state is cleared (a paused run that fails/cancels is no
				// longer continuable).
				if (isTerminalRunEvent(event.eventType)) {
					next = { ...next, pausedNodeId: null, pauseRequestId: null };
				}

				return { runs: { ...state.runs, [event.runId]: next } };
			}),

		reset: () => set({ runs: {} }),
	},
}));
