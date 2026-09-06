import { describe, expect, it } from "vitest";

import {
	asGraphWorkflowDecisionKind,
	asGraphWorkflowEventType,
	GRAPH_WORKFLOW_KEY_PATTERN,
	GRAPH_WORKFLOW_MAX_NODES,
	GRAPH_WORKFLOW_MAX_RENDERED_NODES,
	GRAPH_WORKFLOW_MAX_RUN_INPUT_BYTES,
	graphWorkflowConditionOperators,
	graphWorkflowDecisionKinds,
	graphWorkflowDefaultMaxAttempts,
	graphWorkflowEventTypeLabelKey,
	graphWorkflowEventTypes,
	graphWorkflowFailureClasses,
	graphWorkflowJoinPolicies,
	graphWorkflowNodeKinds,
	graphWorkflowNodeRunStatuses,
	graphWorkflowRunStatuses,
	graphWorkflowTabs,
	isTerminalGraphWorkflowNodeRunStatus,
	isTerminalGraphWorkflowRunStatus,
	narrowGraphWorkflowFailureClass,
	narrowGraphWorkflowJoinPolicy,
	narrowGraphWorkflowNodeKind,
	narrowGraphWorkflowNodeRunStatus,
	narrowGraphWorkflowRunStatus,
	normalizeGraphWorkflowConditionOperator,
	toGraphWorkflowDecisionKinds,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

describe("graph-workflow vocabularies", () => {
	// The member LISTS are the contract with the server: they cross the wire as C# enum names, so a member silently
	// dropped here becomes an unlabelled fallback in front of an operator rather than a compile error.
	it("carries exactly the members the v1 wire uses", () => {
		expect(graphWorkflowNodeKinds).toEqual(["Start", "Agent", "Tool", "Condition", "Parallel", "Join", "Pause", "End"]);
		expect(graphWorkflowJoinPolicies).toEqual(["All", "Any"]);
		expect(graphWorkflowRunStatuses).toHaveLength(7);
		expect(graphWorkflowNodeRunStatuses).toHaveLength(8);
		expect(graphWorkflowDecisionKinds).toEqual(["Approve", "Reject"]);
		expect(graphWorkflowFailureClasses).toHaveLength(9);
		expect(graphWorkflowConditionOperators).toEqual(["Eq", "Ne", "Gt", "Gte", "Lt", "Lte", "Exists", "NotExists"]);
		expect(graphWorkflowEventTypes).toHaveLength(19);
		expect(graphWorkflowTabs).toEqual(["editor", "runs", "events"]);
	});

	it("has no duplicate member in any vocabulary", () => {
		for (const members of [
			graphWorkflowNodeKinds,
			graphWorkflowJoinPolicies,
			graphWorkflowRunStatuses,
			graphWorkflowNodeRunStatuses,
			graphWorkflowDecisionKinds,
			graphWorkflowFailureClasses,
			graphWorkflowConditionOperators,
			graphWorkflowEventTypes,
			graphWorkflowTabs,
		]) {
			expect(new Set<string>(members).size).toBe(members.length);
		}
	});
});

describe("narrowing helpers", () => {
	it("keeps every known member of every vocabulary unchanged", () => {
		for (const kind of graphWorkflowNodeKinds) {
			expect(narrowGraphWorkflowNodeKind(kind)).toBe(kind);
		}
		for (const policy of graphWorkflowJoinPolicies) {
			expect(narrowGraphWorkflowJoinPolicy(policy)).toBe(policy);
		}
		for (const status of graphWorkflowRunStatuses) {
			expect(narrowGraphWorkflowRunStatus(status)).toBe(status);
		}
		for (const status of graphWorkflowNodeRunStatuses) {
			expect(narrowGraphWorkflowNodeRunStatus(status)).toBe(status);
		}
		for (const failureClass of graphWorkflowFailureClasses) {
			expect(narrowGraphWorkflowFailureClass(failureClass)).toBe(failureClass);
		}
	});

	it("falls back to the most inert member for an unknown, empty or absent value", () => {
		for (const value of ["Nonsense", "", undefined, null]) {
			expect(narrowGraphWorkflowNodeKind(value)).toBe("End");
			expect(narrowGraphWorkflowJoinPolicy(value)).toBe("All");
			expect(narrowGraphWorkflowRunStatus(value)).toBe("Pending");
			expect(narrowGraphWorkflowNodeRunStatus(value)).toBe("Pending");
			expect(narrowGraphWorkflowFailureClass(value)).toBe("NodeFailed");
		}
	});

	it("narrows case-sensitively — a lowercase status is not a member", () => {
		expect(narrowGraphWorkflowRunStatus("running")).toBe("Pending");
		expect(narrowGraphWorkflowNodeKind("agent")).toBe("End");
	});
});

describe("asMember helpers", () => {
	it("returns undefined rather than a substitute for an unknown decision or event", () => {
		expect(asGraphWorkflowDecisionKind("RequestChanges")).toBeUndefined();
		expect(asGraphWorkflowDecisionKind(undefined)).toBeUndefined();
		expect(asGraphWorkflowEventType("node.exploded")).toBeUndefined();
		expect(asGraphWorkflowEventType(null)).toBeUndefined();
	});

	it("recognises every real member", () => {
		expect(asGraphWorkflowDecisionKind("Approve")).toBe("Approve");
		expect(asGraphWorkflowDecisionKind("Reject")).toBe("Reject");
		for (const eventType of graphWorkflowEventTypes) {
			expect(asGraphWorkflowEventType(eventType)).toBe(eventType);
		}
	});

	it("drops the members it cannot render from allowedDecisions, keeping the server's order", () => {
		expect(toGraphWorkflowDecisionKinds(["Reject", "Escalate", "Approve"])).toEqual(["Reject", "Approve"]);
		expect(toGraphWorkflowDecisionKinds(undefined)).toEqual([]);
	});
});

describe("normalizeGraphWorkflowConditionOperator", () => {
	it("canonicalises a stored token case-insensitively", () => {
		expect(normalizeGraphWorkflowConditionOperator("eq")).toBe("Eq");
		expect(normalizeGraphWorkflowConditionOperator("NOTEXISTS")).toBe("NotExists");
		expect(normalizeGraphWorkflowConditionOperator("gTe")).toBe("Gte");
	});

	it("returns every canonical token unchanged", () => {
		for (const operator of graphWorkflowConditionOperators) {
			expect(normalizeGraphWorkflowConditionOperator(operator)).toBe(operator);
		}
	});

	it("refuses garbage rather than guessing an operator that would rewrite a branch", () => {
		expect(normalizeGraphWorkflowConditionOperator("equals")).toBeUndefined();
		expect(normalizeGraphWorkflowConditionOperator("==")).toBeUndefined();
		expect(normalizeGraphWorkflowConditionOperator("")).toBeUndefined();
		expect(normalizeGraphWorkflowConditionOperator(undefined)).toBeUndefined();
	});
});

describe("graphWorkflowEventTypeLabelKey", () => {
	it("replaces the dots i18next would read as key separators", () => {
		expect(graphWorkflowEventTypeLabelKey("run.created")).toBe("pages.graphWorkflows.eventType.run_created");
		expect(graphWorkflowEventTypeLabelKey("gate.decided")).toBe("pages.graphWorkflows.eventType.gate_decided");
	});

	it("produces a distinct leaf key for each of the nineteen event types", () => {
		const keys = graphWorkflowEventTypes.map(graphWorkflowEventTypeLabelKey);
		expect(new Set(keys).size).toBe(graphWorkflowEventTypes.length);
		expect(keys.every((key) => !key.slice("pages.graphWorkflows.eventType.".length).includes("."))).toBe(true);
	});
});

describe("terminal-status helpers", () => {
	it("treats only the three settled run statuses as terminal", () => {
		expect(graphWorkflowRunStatuses.filter(isTerminalGraphWorkflowRunStatus)).toEqual(["Completed", "Failed", "Cancelled"]);
	});

	it("treats only the four settled node-run statuses as terminal", () => {
		expect(graphWorkflowNodeRunStatuses.filter(isTerminalGraphWorkflowNodeRunStatus)).toEqual([
			"Succeeded",
			"Failed",
			"Skipped",
			"Cancelled",
		]);
	});
});

describe("server-mirroring constants", () => {
	it("mirrors the node, render and input-size bounds", () => {
		expect(GRAPH_WORKFLOW_MAX_NODES).toBe(200);
		expect(GRAPH_WORKFLOW_MAX_RENDERED_NODES).toBe(200);
		expect(GRAPH_WORKFLOW_MAX_RUN_INPUT_BYTES).toBe(65_536);
	});

	it("accepts the key shapes the server accepts and refuses the rest", () => {
		expect(GRAPH_WORKFLOW_KEY_PATTERN.test("start")).toBe(true);
		expect(GRAPH_WORKFLOW_KEY_PATTERN.test("node_1-A")).toBe(true);
		expect(GRAPH_WORKFLOW_KEY_PATTERN.test("a".repeat(64))).toBe(true);
		expect(GRAPH_WORKFLOW_KEY_PATTERN.test("a".repeat(65))).toBe(false);
		expect(GRAPH_WORKFLOW_KEY_PATTERN.test("")).toBe(false);
		expect(GRAPH_WORKFLOW_KEY_PATTERN.test("has space")).toBe(false);
		expect(GRAPH_WORKFLOW_KEY_PATTERN.test("dot.path")).toBe(false);
	});

	it("defaults maxAttempts to 3 only for the two kinds that call something fallible", () => {
		expect(graphWorkflowDefaultMaxAttempts("Agent")).toBe(3);
		expect(graphWorkflowDefaultMaxAttempts("Tool")).toBe(3);
		for (const kind of graphWorkflowNodeKinds.filter((member) => member !== "Agent" && member !== "Tool")) {
			expect(graphWorkflowDefaultMaxAttempts(kind)).toBe(1);
		}
	});
});
