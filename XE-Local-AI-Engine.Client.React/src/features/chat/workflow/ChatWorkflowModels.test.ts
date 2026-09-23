import { describe, expect, it } from "vitest";

import {
	activeWorkflowNode,
	formatWorkflowElapsed,
	graphAcceptsAttachments,
	isBusyWorkflowRun,
	isLiveWorkflowRun,
	toWorkflowActivity,
	toWorkflowPath,
	workflowNodeFailureReason,
	workflowNodeGlyph,
	workflowNodeWithStatus,
} from "@/features/chat/workflow/ChatWorkflowModels";
import type { GraphWorkflowNodeRunResponse } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import {
	agentNodeRunDetail,
	chatGraph,
	eightNodeGraph,
	graphWorkflowRunEvent,
	makeNodeRun,
} from "@/features/graphWorkflows/test/GraphWorkflowFixtures";

describe("toWorkflowPath", () => {
	it("groups the graph by rank so parallel branches sit side by side, in rank order", () => {
		const path = toWorkflowPath(eightNodeGraph, [
			makeNodeRun({ nodeKey: "start", status: "Succeeded" }),
			makeNodeRun({ nodeKey: "review", status: "Skipped" }),
			makeNodeRun({ nodeKey: "lookup", status: "Running" }),
		]);

		expect(path.map((rank) => rank.map((node) => node.key))).toEqual([
			["start"],
			["analyze"],
			["check"],
			// Same rank, ordered by the layout's own tie-break (the key).
			["lookup", "review"],
			["fanout"],
			["merge"],
			["done"],
		]);
		expect(path[3]?.map((node) => [node.label, node.status])).toEqual([
			["Read file", "Running"],
			["Human review", "Skipped"],
		]);
		// A node with no row yet reads as Pending rather than disappearing.
		expect(path[1]?.[0]?.status).toBe("Pending");
	});

	it("still lists a node run whose key is not in the graph, in a trailing rank", () => {
		const path = toWorkflowPath(chatGraph, [makeNodeRun({ nodeKey: "ghost", kind: "Tool", status: "Failed" })]);

		expect(path.at(-1)?.map((node) => [node.key, node.status])).toEqual([["ghost", "Failed"]]);
	});
});

describe("status helpers", () => {
	it("maps each node status to its glyph", () => {
		expect(
			["Succeeded", "Running", "Queued", "WaitingForApproval", "Pending", "Skipped", "Failed", "Cancelled"].map((status) =>
				workflowNodeGlyph(status as never),
			),
		).toEqual(["✓", "●", "●", "●", "○", "○", "✕", "✕"]);
	});

	it("picks a running node over a queued or parked one as the active node", () => {
		const path = toWorkflowPath(eightNodeGraph, [
			makeNodeRun({ nodeKey: "review", status: "WaitingForApproval" }),
			makeNodeRun({ nodeKey: "lookup", status: "Running" }),
		]);

		expect(activeWorkflowNode(path)?.key).toBe("lookup");
		expect(workflowNodeWithStatus(path, "WaitingForApproval")?.key).toBe("review");
	});

	it("treats a run parked on a ChatInput as live but not busy, and a Pause park as busy", () => {
		expect(isLiveWorkflowRun("WaitingForApproval")).toBe(true);
		expect(isLiveWorkflowRun("Completed")).toBe(false);
		expect(isBusyWorkflowRun("WaitingForApproval", true)).toBe(false);
		expect(isBusyWorkflowRun("WaitingForApproval", false)).toBe(true);
		expect(isBusyWorkflowRun("Running", false)).toBe(true);
		expect(isBusyWorkflowRun("Cancelling", false)).toBe(true);
		expect(isBusyWorkflowRun("Failed", false)).toBe(false);
	});

	it("formats elapsed time and clamps clock skew to zero", () => {
		expect(formatWorkflowElapsed(42_400)).toBe("42s");
		expect(formatWorkflowElapsed(187_000)).toBe("3m 07s");
		expect(formatWorkflowElapsed(3_720_000)).toBe("1h 02m");
		expect(formatWorkflowElapsed(-5_000)).toBe("0s");
	});

	it("reads chat.acceptsAttachments, defaulting to off", () => {
		expect(graphAcceptsAttachments(chatGraph)).toBe(true);
		expect(graphAcceptsAttachments(eightNodeGraph)).toBe(false);
		expect(graphAcceptsAttachments(undefined)).toBe(false);
	});
});

describe("toWorkflowActivity", () => {
	function detail(nodeKey: string, kind: string, output: unknown, extra: Partial<GraphWorkflowNodeRunResponse> = {}) {
		return agentNodeRunDetail({ nodeKey, kind, output, ...extra });
	}

	it("builds routing, intermediate text, the ChatInput question and answer, and publish markers", () => {
		const path = toWorkflowPath(chatGraph, [
			makeNodeRun({ nodeKey: "start", kind: "Start", status: "Succeeded" }),
			makeNodeRun({ nodeKey: "ask", kind: "ChatInput", status: "Succeeded" }),
			makeNodeRun({
				nodeKey: "classify",
				kind: "DecisionModel",
				status: "Succeeded",
				startedAtUtc: 1_000,
				completedAtUtc: 4_000,
			}),
			makeNodeRun({ nodeKey: "code", kind: "LlmCall", status: "Succeeded" }),
			makeNodeRun({ nodeKey: "done", kind: "End", status: "Pending", startedAtUtc: null, completedAtUtc: null }),
		]);
		const details = new Map([
			[
				"ask",
				detail("ask", "ChatInput", {
					status: "succeeded",
					attempt: 1,
					branch: null,
					output: { decision: "Answer", text: "A CLI" },
				}),
			],
			[
				"classify",
				detail("classify", "DecisionModel", {
					status: "succeeded",
					attempt: 1,
					branch: "coding",
					output: { choice: "coding", provider: "llm" },
				}),
			],
			["code", detail("code", "LlmCall", { status: "succeeded", attempt: 1, branch: null, output: { text: "fn main() {}" } })],
		]);
		const events = [graphWorkflowRunEvent({ eventType: "node.published", nodeKey: "code", detail: { messageId: "m" } })];

		const entries = toWorkflowActivity(chatGraph, path, details, events);

		// `done` never ran, so it is not an activity row.
		expect(entries.map((entry) => entry.key)).toEqual(["start", "ask", "classify", "code"]);
		expect(entries[1]?.input).toEqual({ prompt: "What should I build?", answer: "A CLI" });
		expect(entries[2]).toMatchObject({ route: "coding", durationMs: 3_000, published: false });
		expect(entries[3]).toMatchObject({ text: "fn main() {}", published: true });
	});

	it("never shows a lane queue token as an error, and shows a real reason only on a failed node", () => {
		expect(workflowNodeFailureReason("Succeeded", "awaiting-agent-slot")).toBeUndefined();
		expect(workflowNodeFailureReason("Failed", "awaiting-invocation-slot")).toBeUndefined();
		expect(workflowNodeFailureReason("Queued", "awaiting-invocation-slot")).toBeUndefined();
		expect(workflowNodeFailureReason("Failed", "The model refused the schema.")).toBe("The model refused the schema.");

		const path = toWorkflowPath(chatGraph, [makeNodeRun({ nodeKey: "code", kind: "LlmCall", status: "Succeeded" })]);
		const details = new Map([
			[
				"code",
				agentNodeRunDetail({
					nodeKey: "code",
					kind: "LlmCall",
					error: "awaiting-invocation-slot",
					output: { status: "succeeded", attempt: 1, branch: null, output: { text: "done" } },
				}),
			],
		]);
		expect(toWorkflowActivity(chatGraph, path, details, [])[0]?.error).toBeUndefined();
	});

	it("summarises a Tool result under its tool name and reports a Condition's branch", () => {
		const path = toWorkflowPath(eightNodeGraph, [
			makeNodeRun({ nodeKey: "check", kind: "Condition", status: "Succeeded" }),
			makeNodeRun({ nodeKey: "lookup", kind: "Tool", status: "Succeeded" }),
		]);
		const details = new Map([
			["check", detail("check", "Condition", { status: "succeeded", attempt: 1, branch: "no", output: {} })],
			["lookup", detail("lookup", "Tool", { status: "succeeded", attempt: 1, branch: null, output: { result: { lines: 12 } } })],
		]);

		const entries = toWorkflowActivity(eightNodeGraph, path, details, []);

		expect(entries.find((entry) => entry.key === "check")?.route).toBe("no");
		expect(entries.find((entry) => entry.key === "lookup")?.tool).toEqual({ name: "read_file", summary: '{"lines":12}' });
	});
});
