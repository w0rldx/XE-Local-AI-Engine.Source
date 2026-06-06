import { beforeEach, describe, expect, it } from "vitest";

import { previewHubEvents } from "@/features/preview/models/PreviewWorkflowModels";
import { usePreviewRunStore } from "@/features/preview/stores/PreviewRunStore";

// Resets the store between tests so each starts from the empty (mount/reload) state.
function resetStore(): void {
	usePreviewRunStore.getState().actions.reset();
}

const RUN_A = "run-a";
const RUN_B = "run-b";

function nodeEvent(runId: string, nodeId: string, eventType: string, output?: string) {
	return { eventType, runId, nodeId, output: output ?? null, error: null, occurredAtUtc: 1 };
}

describe("PreviewRunStore", () => {
	beforeEach(resetStore);

	it("applies node output keyed by (runId, nodeId) for a registered run", () => {
		const { actions } = usePreviewRunStore.getState();
		actions.registerRun(RUN_A);

		actions.applyNodeEvent(nodeEvent(RUN_A, "agent-1", previewHubEvents.nodeOutput, "Hello "));
		actions.applyNodeEvent(nodeEvent(RUN_A, "agent-1", previewHubEvents.nodeOutput, "world"));

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
});
