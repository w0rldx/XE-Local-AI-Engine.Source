// A fresh node's defaults, key minting and the rename cascade.

import { describe, expect, it } from "vitest";

import {
	defaultNodeData,
	mintEdgeKey,
	mintNodeKey,
	renameNodeKey,
} from "@/features/graphWorkflows/models/GraphWorkflowAuthoring";
import { graphToCanvas } from "@/features/graphWorkflows/models/GraphWorkflowCanvasConversion";
import { eightNodeGraph } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";

describe("renameNodeKey", () => {
	it("rewrites the node id, its data key and every edge endpoint", () => {
		const canvas = graphToCanvas(eightNodeGraph);
		const result = renameNodeKey(canvas.nodes, canvas.edges, "review", "human-review");

		expect("error" in result).toBe(false);
		if ("error" in result) {
			return;
		}
		const renamed = result.nodes.find((node) => node.id === "human-review");
		expect(renamed?.data.key).toBe("human-review");
		expect(result.nodes.some((node) => node.id === "review")).toBe(false);
		expect(result.edges.find((edge) => edge.id === "e5")?.source).toBe("human-review");
		expect(result.edges.find((edge) => edge.id === "e3")?.target).toBe("human-review");
	});

	it("leaves a Pause out-edge's condition value alone — it is a decision, not a node key", () => {
		const canvas = graphToCanvas(eightNodeGraph);
		const result = renameNodeKey(canvas.nodes, canvas.edges, "review", "human-review");

		expect("error" in result).toBe(false);
		if ("error" in result) {
			return;
		}
		const approve = result.edges.find((edge) => edge.id === "e5");
		expect(approve?.source).toBe("human-review");
		expect(approve?.data?.condition?.value).toBe('"Approve"');
		expect(result.edges.find((edge) => edge.id === "e9")?.data?.condition?.value).toBe('"Reject"');
	});

	it("refuses a name held by another node or by an edge, and one outside the server's charset", () => {
		const canvas = graphToCanvas(eightNodeGraph);

		expect(renameNodeKey(canvas.nodes, canvas.edges, "review", "lookup")).toEqual({ error: "collision" });
		expect(renameNodeKey(canvas.nodes, canvas.edges, "review", "e1")).toEqual({ error: "collision" });
		expect(renameNodeKey(canvas.nodes, canvas.edges, "review", "human review")).toEqual({ error: "invalid" });
		expect(renameNodeKey(canvas.nodes, canvas.edges, "review", "")).toEqual({ error: "invalid" });
	});

	it("accepts a rename to the key the node already holds", () => {
		const canvas = graphToCanvas(eightNodeGraph);
		const result = renameNodeKey(canvas.nodes, canvas.edges, "review", "review");

		expect("error" in result).toBe(false);
		if ("error" in result) {
			return;
		}
		expect(result.nodes.some((node) => node.id === "review")).toBe(true);
	});
});

describe("defaultNodeData and key minting", () => {
	it("starts Agent and Tool nodes on three attempts and every other kind on one", () => {
		expect(defaultNodeData("DecisionModel", "decision-model-1").maxAttempts).toBe(3);
		expect(defaultNodeData("ChatInput", "chat-input-1").maxAttempts).toBe(1);
		expect(defaultNodeData("Agent", "agent-1").maxAttempts).toBe(3);
		expect(defaultNodeData("Tool", "tool-1").maxAttempts).toBe(3);
		expect(defaultNodeData("Condition", "condition-1").maxAttempts).toBe(1);
		expect(defaultNodeData("Parallel", "parallel-1").maxAttempts).toBe(1);
	});

	it("seeds each kind with its pinned defaults", () => {
		const pause = defaultNodeData("Pause", "pause-1");
		expect(pause).toMatchObject({ kind: "Pause", allowedDecisions: ["Approve", "Reject"], requireComment: false });
		expect(defaultNodeData("Agent", "agent-1")).toMatchObject({ includeUpstreamOutputs: true, instructions: "" });
		expect(defaultNodeData("End", "done")).toMatchObject({ outcome: "completed", resultPath: null });
		expect(defaultNodeData("Start", "start")).toMatchObject({ inputSchema: null, defaultInput: null });
		expect(defaultNodeData("Join", "merge")).toMatchObject({ kind: "Join", joinPolicy: "All", label: "" });
		expect(defaultNodeData("Condition", "condition-1")).toMatchObject({ path: null });
		expect(defaultNodeData("ChatInput", "chat-input-1")).toMatchObject({ kind: "ChatInput", prompt: "" });
		expect(defaultNodeData("DecisionModel", "decision-model-1")).toMatchObject({
			question: "",
			labels: [],
			provider: null,
			model: null,
			inputBindings: [],
		});
		// No chat flag on a fresh node: absent is the parser's default, and a Standard graph must not grow one.
		expect("publishToChat" in defaultNodeData("End", "done")).toBe(false);
	});

	it("mints the lowest free integer over the ONE key namespace", () => {
		expect(mintNodeKey("Agent", ["agent-1", "agent-3"])).toBe("agent-2");
		expect(mintNodeKey("Agent", [])).toBe("agent-1");
		expect(mintEdgeKey(["e1", "e2"])).toBe("e3");
		// An edge may not take a key a node holds, so the caller passes both sets and `e2` is skipped.
		expect(mintEdgeKey(["e1", "e2", "agent-1"])).toBe("e3");
	});
});
