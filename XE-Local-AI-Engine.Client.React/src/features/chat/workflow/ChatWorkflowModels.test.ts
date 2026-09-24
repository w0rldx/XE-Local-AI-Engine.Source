import { describe, expect, it } from "vitest";

import {
	activeWorkflowNode,
	formatWorkflowElapsed,
	graphAcceptsAttachments,
	isBusyWorkflowRun,
	isLiveWorkflowRun,
	liveWorkflowNode,
	steerableWorkflowNode,
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

	it("streams the live output of a running Agent, LLM Call or DecisionModel that has an invocation, and nothing else", () => {
		const running = (nodeKey: string, kind: string, invocationId: string | null) =>
			toWorkflowPath(chatGraph, [makeNodeRun({ nodeKey, kind, status: "Running", invocationId })]);

		expect(liveWorkflowNode(running("classify", "DecisionModel", "inv-1"))?.invocationId).toBe("inv-1");
		expect(liveWorkflowNode(running("code", "LlmCall", "inv-2"))?.key).toBe("code");
		expect(liveWorkflowNode(running("code", "LlmCall", null))).toBeUndefined();
		expect(liveWorkflowNode(running("ask", "ChatInput", "inv-3"))).toBeUndefined();
	});

	it("offers a steer on a running or queued Agent / LLM Call only", () => {
		const active = (nodeKey: string, kind: string, status: string) =>
			steerableWorkflowNode(toWorkflowPath(chatGraph, [makeNodeRun({ nodeKey, kind, status })]));

		expect(active("code", "LlmCall", "Running")?.key).toBe("code");
		expect(active("code", "LlmCall", "Queued")?.key).toBe("code");
		expect(active("code", "LlmCall", "Succeeded")).toBeUndefined();
		expect(active("classify", "DecisionModel", "Running")).toBeUndefined();
		expect(active("ask", "ChatInput", "WaitingForApproval")).toBeUndefined();
		expect(
			steerableWorkflowNode(
				toWorkflowPath(eightNodeGraph, [makeNodeRun({ nodeKey: "analyze", kind: "Agent", status: "Running" })]),
			)?.key,
		).toBe("analyze");
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

	it("lists the attachments an Agent turn went without, and nothing when none were skipped", () => {
		const path = toWorkflowPath(chatGraph, [
			makeNodeRun({ nodeKey: "code", kind: "LlmCall", status: "Succeeded" }),
			makeNodeRun({ nodeKey: "classify", kind: "DecisionModel", status: "Succeeded" }),
		]);
		const details = new Map([
			[
				"code",
				detail("code", "LlmCall", {
					status: "succeeded",
					attempt: 1,
					branch: null,
					output: { text: "done", attachmentsSkipped: ["a.pdf", "b.png"] },
				}),
			],
			[
				"classify",
				detail("classify", "DecisionModel", { status: "succeeded", attempt: 1, branch: null, output: { choice: "x" } }),
			],
		]);

		const entries = toWorkflowActivity(chatGraph, path, details, []);

		expect(entries.find((entry) => entry.key === "code")?.attachmentsSkipped).toEqual(["a.pdf", "b.png"]);
		expect(entries.find((entry) => entry.key === "classify")).not.toHaveProperty("attachmentsSkipped");
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

	it("marks each steer under its node — applied with its text, or arrived after the node finished", () => {
		const path = toWorkflowPath(chatGraph, [makeNodeRun({ nodeKey: "code", kind: "LlmCall", status: "Succeeded" })]);
		const details = new Map([
			[
				"code",
				{
					...detail("code", "LlmCall", { status: "succeeded", attempt: 1, branch: null, output: { text: "done" } }),
					steering: [{ operationId: "op-2", message: "Use Rust instead", atUtc: 2, attempt: 1, applied: true }],
				},
			],
		]);
		const events = [
			graphWorkflowRunEvent({ seq: 5, eventType: "node.steer-ignored", nodeKey: "code", detail: { operationId: "op-3" } }),
			graphWorkflowRunEvent({ seq: 2, eventType: "node.steered", nodeKey: "code", detail: { message: "Be brief" } }),
			graphWorkflowRunEvent({ seq: 4, eventType: "node.steered", nodeKey: "code", detail: { operationId: "op-2" } }),
		];

		const [entry] = toWorkflowActivity(chatGraph, path, details, events);

		expect(entry?.steering).toEqual([
			{ seq: 2, applied: true, message: "Be brief" },
			{ seq: 4, applied: true, message: "Use Rust instead" },
			{ seq: 5, applied: false },
		]);
	});
});
