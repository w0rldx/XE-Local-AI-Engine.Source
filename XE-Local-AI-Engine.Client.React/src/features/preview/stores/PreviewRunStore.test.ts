import { beforeEach, describe, expect, it } from "vitest";

import { previewHubEvents } from "@/features/preview/models/PreviewWorkflowModels";
import { usePreviewRunStore } from "@/features/preview/stores/PreviewRunStore";

// Resets the store between tests so each starts from the empty (mount/reload) state.
function resetStore(): void {
	usePreviewRunStore.getState().actions.reset();
}

const RUN_A = "run-a";
const RUN_B = "run-b";

function nodeEvent(runId: string, nodeId: string, eventType: string, output?: string, seq = 0) {
	return { eventType, runId, nodeId, output: output ?? null, error: null, occurredAtUtc: 1, seq };
}

describe("PreviewRunStore", () => {
	beforeEach(resetStore);

	it("applies node output keyed by (runId, nodeId) for a registered run", () => {
		const { actions } = usePreviewRunStore.getState();
		actions.registerRun(RUN_A);

		actions.applyNodeEvent(nodeEvent(RUN_A, "agent-1", previewHubEvents.nodeOutput, "Hello ", 0));
		actions.applyNodeEvent(nodeEvent(RUN_A, "agent-1", previewHubEvents.nodeOutput, "world", 1));

		const state = usePreviewRunStore.getState().runs[RUN_A]?.nodes["agent-1"];
		expect(state?.output).toBe("Hello world");
		expect(state?.status).toBe("running");
	});

	it("ignores events for a runId this tab did not register (foreign-run guard)", () => {
		const { actions } = usePreviewRunStore.getState();
		actions.registerRun(RUN_A);

		// Event for an UNREGISTERED run must be dropped — never creates state for RUN_B.
		actions.applyNodeEvent(nodeEvent(RUN_B, "agent-1", previewHubEvents.nodeOutput, "leak"));

		expect(usePreviewRunStore.getState().runs[RUN_B]).toBeUndefined();
		expect(usePreviewRunStore.getState().runs[RUN_A]?.nodes["agent-1"]).toBeUndefined();
	});

	it("keeps two registered runs isolated by runId", () => {
		const { actions } = usePreviewRunStore.getState();
		actions.registerRun(RUN_A);
		actions.registerRun(RUN_B);

		actions.applyNodeEvent(nodeEvent(RUN_A, "agent-1", previewHubEvents.nodeOutput, "A-output"));
		actions.applyNodeEvent(nodeEvent(RUN_B, "agent-1", previewHubEvents.nodeOutput, "B-output"));

		expect(usePreviewRunStore.getState().runs[RUN_A]?.nodes["agent-1"]?.output).toBe("A-output");
		expect(usePreviewRunStore.getState().runs[RUN_B]?.nodes["agent-1"]?.output).toBe("B-output");
	});

	it("stores the debug node's upstream output separately from node output", () => {
		const { actions } = usePreviewRunStore.getState();
		actions.registerRun(RUN_A);

		actions.applyNodeEvent(nodeEvent(RUN_A, "debug-1", previewHubEvents.nodeDebug, "raw upstream"));

		expect(usePreviewRunStore.getState().runs[RUN_A]?.nodes["debug-1"]?.debugOutput).toBe("raw upstream");
	});

	it("tracks run lifecycle (paused → continue token) for a registered run", () => {
		const { actions } = usePreviewRunStore.getState();
		actions.registerRun(RUN_A);

		actions.applyRunEvent({
			eventType: previewHubEvents.runPaused,
			runId: RUN_A,
			nodeId: "pause-1",
			output: "upstream",
			error: null,
			requestId: "resume-token",
			occurredAtUtc: 1,
			seq: 0,
		});

		const run = usePreviewRunStore.getState().runs[RUN_A];
		expect(run?.status).toBe("paused");
		expect(run?.pausedNodeId).toBe("pause-1");
		expect(run?.pauseRequestId).toBe("resume-token");
	});

	it("clears all runs on reset (empty on mount/reload)", () => {
		const { actions } = usePreviewRunStore.getState();
		actions.registerRun(RUN_A);
		actions.applyNodeEvent(nodeEvent(RUN_A, "agent-1", previewHubEvents.nodeOutput, "x"));

		actions.reset();

		expect(usePreviewRunStore.getState().runs).toEqual({});
	});

	it("does NOT double nodeOutput when a replayed event (same seq) is applied twice", () => {
		const { actions } = usePreviewRunStore.getState();
		actions.registerRun(RUN_A);

		const event = nodeEvent(RUN_A, "agent-1", previewHubEvents.nodeOutput, "Hello ", 0);
		actions.applyNodeEvent(event);
		// Same seq arriving again (e.g. backend replay racing the live group broadcast) must be dropped.
		actions.applyNodeEvent(event);

		expect(usePreviewRunStore.getState().runs[RUN_A]?.nodes["agent-1"]?.output).toBe("Hello ");
	});

	it("applies a new seq normally after a duplicate of an earlier seq was dropped", () => {
		const { actions } = usePreviewRunStore.getState();
		actions.registerRun(RUN_A);

		actions.applyNodeEvent(nodeEvent(RUN_A, "agent-1", previewHubEvents.nodeOutput, "Hello ", 0));
		actions.applyNodeEvent(nodeEvent(RUN_A, "agent-1", previewHubEvents.nodeOutput, "Hello ", 0)); // duplicate, dropped
		actions.applyNodeEvent(nodeEvent(RUN_A, "agent-1", previewHubEvents.nodeOutput, "world", 1)); // new seq, applied

		expect(usePreviewRunStore.getState().runs[RUN_A]?.nodes["agent-1"]?.output).toBe("Hello world");
	});

	it("applies out-of-order seqs exactly once each, then drops both on redelivery", () => {
		const { actions } = usePreviewRunStore.getState();
		actions.registerRun(RUN_A);

		// seq 2 arrives (e.g. live) before seq 1 (e.g. replay) — neither is a duplicate yet, so both must apply.
		actions.applyNodeEvent(nodeEvent(RUN_A, "agent-1", previewHubEvents.nodeOutput, "second-", 2));
		actions.applyNodeEvent(nodeEvent(RUN_A, "agent-1", previewHubEvents.nodeOutput, "first-", 1));

		expect(usePreviewRunStore.getState().runs[RUN_A]?.nodes["agent-1"]?.output).toBe("second-first-");

		// Re-delivery of either seq (replay racing live, or vice versa) must now be dropped as a duplicate.
		actions.applyNodeEvent(nodeEvent(RUN_A, "agent-1", previewHubEvents.nodeOutput, "first-", 1));
		actions.applyNodeEvent(nodeEvent(RUN_A, "agent-1", previewHubEvents.nodeOutput, "second-", 2));

		expect(usePreviewRunStore.getState().runs[RUN_A]?.nodes["agent-1"]?.output).toBe("second-first-");
	});

	it("sets run status to completed on a terminal run event", () => {
		const { actions } = usePreviewRunStore.getState();
		actions.registerRun(RUN_A);

		actions.applyRunEvent({
			eventType: previewHubEvents.runCompleted,
			runId: RUN_A,
			nodeId: null,
			output: "final",
			error: null,
			requestId: null,
			occurredAtUtc: 1,
			seq: 0,
		});

		const run = usePreviewRunStore.getState().runs[RUN_A];
		expect(run?.status).toBe("completed");
		expect(run?.finalOutput).toBe("final");
	});

	it("marks a run cancelled locally via markCancelled (optimistic Cancel button hide)", () => {
		const { actions } = usePreviewRunStore.getState();
		actions.registerRun(RUN_A);

		actions.markCancelled(RUN_A);

		expect(usePreviewRunStore.getState().runs[RUN_A]?.status).toBe("cancelled");
	});

	it("ignores markCancelled for a runId this tab did not register (foreign-run guard)", () => {
		const { actions } = usePreviewRunStore.getState();

		actions.markCancelled(RUN_B);

		expect(usePreviewRunStore.getState().runs[RUN_B]).toBeUndefined();
	});
});
