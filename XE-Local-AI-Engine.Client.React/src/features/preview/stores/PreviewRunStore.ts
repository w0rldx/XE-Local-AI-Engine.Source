import { create } from "zustand";

import {
	isTerminalRunEvent,
	type PreviewNodeEvent,
	previewHubEvents,
	type PreviewRunEvent,
} from "@/features/preview/models/PreviewWorkflowModels";

// Transient run-output store for the Open Canvas (Preview) surface. State is keyed by `(runId, nodeId)` so two
// runs in flight on the same hub connection never cross-contaminate: each Preview tab
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
	// Exactly-once, order-independent seq dedupe (node AND run events share one per-run sequence). The backend
	// buffers events and REPLAYS them to a connection on Subscribe(runId), so a replayed event can arrive again
	// after the same event was already applied live (or vice versa) — and the two channels (replay via the
	// Caller, live via the Group) can interleave out of order. A high-water-mark + bounded gap set gives O(1)
	// amortized dedupe without an ever-growing "every seq ever seen" set (a long streaming run has unbounded
	// nodeOutput chunks, up to MaxOutputBytes):
	//   - lastContiguousSeq: the highest seq N such that every seq <= N has been applied. Anything <= this is a
	//     duplicate.
	//   - pendingSeqs: seqs applied OUT OF ORDER (above lastContiguousSeq + 1) that haven't been "absorbed" into
	//     the contiguous run yet. Membership here is also a duplicate. This set holds only the (normally tiny)
	//     reorder gap, not the full history.
	readonly lastContiguousSeq: number;
	readonly pendingSeqs: ReadonlySet<number>;
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
		// Optimistic local cancel (defense-in-depth for the Cancel button hiding immediately, in case the
		// authoritative `runCancelled` hub event is somehow delayed/missed). Idempotent with the hub event
		// arriving later: this sets status directly rather than going through seq dedupe.
		markCancelled: (runId: string) => void;
		// Clear ALL run state (called on page unmount so a reload starts empty).
		reset: () => void;
	};
}

const EMPTY_NODE_STATE: PreviewNodeRunState = { status: "idle", output: "", debugOutput: null, error: null };

function emptyRunState(): PreviewRunState {
	return {
		status: "running",
		pausedNodeId: null,
		pauseRequestId: null,
		pauseOutput: null,
		finalOutput: null,
		error: null,
		nodes: {},
		lastContiguousSeq: -1,
		pendingSeqs: new Set(),
	};
}

function updateNode(
	run: PreviewRunState,
	nodeId: string,
	patch: Partial<PreviewNodeRunState>,
): PreviewRunState {
	const current = run.nodes[nodeId] ?? EMPTY_NODE_STATE;
	return { ...run, nodes: { ...run.nodes, [nodeId]: { ...current, ...patch } } };
}

// True if `seq` has already been applied to `run` — either it's at or behind the contiguous high-water-mark, or it
// was already absorbed as an out-of-order arrival sitting in the gap set.
function isDuplicateSeq(run: PreviewRunState, seq: number): boolean {
	return seq <= run.lastContiguousSeq || run.pendingSeqs.has(seq);
}

// Records `seq` as applied and returns the updated seq-tracking fields. If `seq` extends the contiguous run
// (lastContiguousSeq + 1), the high-water-mark advances and then drains any now-consecutive values out of the gap
// set; otherwise `seq` is an out-of-order arrival and is parked in the (normally tiny) gap set until the seqs
// between it and the high-water-mark show up.
function advanceSeq(run: PreviewRunState, seq: number): Pick<PreviewRunState, "lastContiguousSeq" | "pendingSeqs"> {
	if (seq !== run.lastContiguousSeq + 1) {
		return { lastContiguousSeq: run.lastContiguousSeq, pendingSeqs: new Set(run.pendingSeqs).add(seq) };
	}
	const pendingSeqs = new Set(run.pendingSeqs);
	let lastContiguousSeq = seq;
	while (pendingSeqs.delete(lastContiguousSeq + 1)) {
		lastContiguousSeq += 1;
	}
	return { lastContiguousSeq, pendingSeqs };
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
				// Exactly-once dedupe: a replayed event (backend replay-on-Subscribe) applied twice would double
				// `output` (it accumulates). Drop it if this seq was already applied, regardless of arrival order
				// between replay and the live group broadcast.
				if (isDuplicateSeq(run, event.seq)) {
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
				next = { ...next, ...advanceSeq(run, event.seq) };

				return { runs: { ...state.runs, [event.runId]: next } };
			}),

		applyRunEvent: (event) =>
			set((state) => {
				const run = state.runs[event.runId];
				if (run === undefined) {
					return state;
				}
				// Exactly-once dedupe — see the identical guard in applyNodeEvent (seq is shared across both event
				// kinds for a run).
				if (isDuplicateSeq(run, event.seq)) {
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
				next = { ...next, ...advanceSeq(run, event.seq) };

				return { runs: { ...state.runs, [event.runId]: next } };
			}),

		// Optimistic local cancel for the Cancel button (defense-in-depth alongside the authoritative
		// `runCancelled` hub event, which is now reliably replayed but could in principle be delayed). Sets
		// status directly rather than through seq dedupe — a later-arriving hub event is still applied
		// normally (status "cancelled" → "cancelled" is a no-op) and its seq is still recorded.
		markCancelled: (runId) =>
			set((state) => {
				const run = state.runs[runId];
				if (run === undefined) {
					return state;
				}
				return {
					runs: {
						...state.runs,
						[runId]: { ...run, status: "cancelled", pausedNodeId: null, pauseRequestId: null },
					},
				};
			}),

		reset: () => set({ runs: {} }),
	},
}));
