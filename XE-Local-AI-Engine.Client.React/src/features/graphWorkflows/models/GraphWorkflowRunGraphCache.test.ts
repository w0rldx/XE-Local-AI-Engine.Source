// The run hub invalidates the run detail on EVERY event, and that detail now carries the run's pinned graph — up to a
// MiB of it. So the parse-and-layout half of the run canvas has to be keyed on the graph's identity, or a node
// transition that only moves one badge re-ranks the whole document.
//
// Its own file because the spy has to replace `graphToCanvas` at module load, and the main suite asserts the real
// conversion's output.

import { beforeEach, describe, expect, it, vi } from "vitest";

import { graphToCanvas } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import type { GraphWorkflowGraph } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { toGraphWorkflowRunCanvas } from "@/features/graphWorkflows/models/GraphWorkflowRunGraph";
import { eightNodeGraph, graphWorkflowRun, graphWorkflowRunSummary, makeNodeRun } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";

vi.mock("@/features/graphWorkflows/models/GraphWorkflowCanvasModels", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/features/graphWorkflows/models/GraphWorkflowCanvasModels")>();
	return { ...actual, graphToCanvas: vi.fn(actual.graphToCanvas) };
});

const converted = vi.mocked(graphToCanvas);
const run = graphWorkflowRunSummary();
const nodeRuns = graphWorkflowRun().nodeRuns ?? [];

/** A fresh object per test, so one test's cache entry is never what makes the next one pass. */
function pinned(): GraphWorkflowGraph {
	return structuredClone(eightNodeGraph);
}

beforeEach(() => {
	converted.mockClear();
});

describe("the run canvas converts a pinned graph once per graph object", () => {
	it("does not re-parse or re-lay-out the graph when only the node runs tick", () => {
		const graph = pinned();

		const first = toGraphWorkflowRunCanvas({ run, nodeRuns, runGraph: graph });
		// What a hub event looks like: the same graph object back from structural sharing, new node-run rows.
		const ticked = toGraphWorkflowRunCanvas({
			run,
			nodeRuns: nodeRuns.map((row) => ({ ...row, status: "Running", updatedAtUtc: 1 })),
			runGraph: graph,
		});

		expect(converted).toHaveBeenCalledTimes(1);
		// The overlay still moved — otherwise this would pass on a canvas that ignored the rows entirely.
		expect(first.nodes.find((node) => node.id === "start")?.data.runState?.status).toBe("Succeeded");
		expect(ticked.nodes.find((node) => node.id === "start")?.data.runState?.status).toBe("Running");
	});

	it("converts again for a genuinely different graph object", () => {
		toGraphWorkflowRunCanvas({ run, nodeRuns, runGraph: pinned() });
		toGraphWorkflowRunCanvas({ run, nodeRuns, runGraph: pinned() });

		expect(converted).toHaveBeenCalledTimes(2);
	});

	it("hands back nodes the caller may annotate without poisoning the cache", () => {
		const graph = pinned();
		const rows = [makeNodeRun({ nodeKey: "start", kind: "Start", status: "Failed" })];

		const withFailure = toGraphWorkflowRunCanvas({ run, nodeRuns: rows, runGraph: graph });
		const withNothing = toGraphWorkflowRunCanvas({ run, nodeRuns: [], runGraph: graph });

		expect(withFailure.nodes.find((node) => node.id === "start")?.data.runState?.status).toBe("Failed");
		// The second call shares the cached conversion; a run state written into it would leak across runs.
		expect(withNothing.nodes.find((node) => node.id === "start")?.data.runState).toBeUndefined();
	});
});
