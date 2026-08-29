// The WIRE-VOCABULARY guard. Every union here is a ruling (X4 statuses, Y6's seven node types, X3's six decision
// kinds, C24's ten artifact kinds, Y4's five work-item statuses), and every one of them is looked up as an i18n key
// after narrowing — so a member added on the server and not here renders the FALLBACK, silently. The literal lists
// are asserted rather than inferred for exactly that reason.

import { describe, expect, it } from "vitest";

import {
	asDevWorkflowArtifactKind,
	asDevWorkflowDecisionKind,
	decodeDevWorkflowArtifactContent,
	devWorkflowArtifactKinds,
	devWorkflowArtifactLanguage,
	devWorkflowDecisionKinds,
	devWorkflowNodeAwaitsHuman,
	devWorkflowNodeStatuses,
	devWorkflowNodeTypes,
	devWorkflowRunStatuses,
	devWorkflowWorkItemStatuses,
	isActiveDevWorkflowRunStatus,
	isDevWorkflowNodeInProgress,
	isTerminalDevWorkflowRunStatus,
	toDevWorkflowDecisionKinds,
	toDevWorkflowNodeStatus,
	toDevWorkflowNodeType,
	toDevWorkflowRunStatus,
	toDevWorkflowWorkItemStatus,
} from "@/features/devWorkflows/models/DevWorkflowModels";

describe("dev-workflow wire unions", () => {
	it("carries X4's nine node-run statuses verbatim", () => {
		expect([...devWorkflowNodeStatuses]).toEqual([
			"Pending",
			"Queued",
			"Running",
			"WaitingForApproval",
			"Blocked",
			"Succeeded",
			"Failed",
			"Skipped",
			"Cancelled",
		]);
	});

	it("carries X4's nine run statuses and deliberately no Interrupted", () => {
		expect([...devWorkflowRunStatuses]).toEqual([
			"Pending",
			"Running",
			"Pausing",
			"Paused",
			"WaitingForApproval",
			"Cancelling",
			"Completed",
			"Failed",
			"Cancelled",
		]);
		expect(devWorkflowRunStatuses).not.toContain("Interrupted");
	});

	it("carries Y6's SEVEN node types with Start and End implicit", () => {
		expect([...devWorkflowNodeTypes]).toEqual(["Agent", "Tool", "DevTask", "HumanGate", "Gate", "Parallel", "Join"]);
	});

	it("carries X3's six decision kinds, gates and interventions on one enum", () => {
		expect([...devWorkflowDecisionKinds]).toEqual(["Approve", "Reject", "RequestChanges", "Retry", "Skip", "Abandon"]);
	});

	it("carries P1's ten artifact kinds, not the work-session set", () => {
		expect([...devWorkflowArtifactKinds]).toEqual([
			"Research",
			"Decision",
			"Specification",
			"Plan",
			"TaskPackage",
			"Patch",
			"Report",
			"Finding",
			"ValidationReport",
			"ReviewReport",
		]);
	});

	it("carries Y4's five work-item statuses", () => {
		expect([...devWorkflowWorkItemStatuses]).toEqual(["Draft", "Active", "Blocked", "Completed", "Cancelled"]);
	});
});

describe("dev-workflow narrowing", () => {
	it("passes every known member through untouched", () => {
		for (const status of devWorkflowNodeStatuses) {
			expect(toDevWorkflowNodeStatus(status)).toBe(status);
		}
		for (const status of devWorkflowRunStatuses) {
			expect(toDevWorkflowRunStatus(status)).toBe(status);
		}
		for (const type of devWorkflowNodeTypes) {
			expect(toDevWorkflowNodeType(type)).toBe(type);
		}
		for (const status of devWorkflowWorkItemStatuses) {
			expect(toDevWorkflowWorkItemStatus(status)).toBe(status);
		}
	});

	it("reads an unknown or absent node status as Pending — the state with no controls", () => {
		expect(toDevWorkflowNodeStatus("Exploded")).toBe("Pending");
		expect(toDevWorkflowNodeStatus(undefined)).toBe("Pending");
		expect(toDevWorkflowNodeStatus(null)).toBe("Pending");
		// The round-1 rename traps: neither old spelling may resolve to itself.
		expect(toDevWorkflowNodeStatus("WaitingForHuman")).toBe("Pending");
		expect(toDevWorkflowNodeStatus("Completed")).toBe("Pending");
	});

	it("reads an unknown run status as Pending, so no lifecycle command is offered for it", () => {
		expect(toDevWorkflowRunStatus("Interrupted")).toBe("Pending");
		expect(toDevWorkflowRunStatus(undefined)).toBe("Pending");
	});

	it("reads an unknown node type as Gate — the structural panel with no link-outs", () => {
		expect(toDevWorkflowNodeType("Subworkflow")).toBe("Gate");
		expect(toDevWorkflowNodeType("Start")).toBe("Gate");
		expect(toDevWorkflowNodeType(undefined)).toBe("Gate");
	});

	it("reads an unknown work-item status as Draft", () => {
		expect(toDevWorkflowWorkItemStatus("Archived")).toBe("Draft");
		expect(toDevWorkflowWorkItemStatus(undefined)).toBe("Draft");
	});

	it("drops an unrecognised decision kind rather than substituting one", () => {
		expect(asDevWorkflowDecisionKind("Approve")).toBe("Approve");
		expect(asDevWorkflowDecisionKind("Escalate")).toBeUndefined();
		expect(asDevWorkflowDecisionKind(undefined)).toBeUndefined();
	});

	it("filters allowedDecisions to renderable members, preserving the server's order", () => {
		expect(toDevWorkflowDecisionKinds(["Reject", "Escalate", "Approve"])).toEqual(["Reject", "Approve"]);
		expect(toDevWorkflowDecisionKinds(undefined)).toEqual([]);
	});

	it("drops an unrecognised artifact kind rather than mislabelling it", () => {
		expect(asDevWorkflowArtifactKind("ValidationReport")).toBe("ValidationReport");
		expect(asDevWorkflowArtifactKind("Note")).toBeUndefined();
	});
});

describe("dev-workflow status predicates", () => {
	it("counts Pausing and Cancelling as active — work is still winding down behind a fire-and-forget command", () => {
		expect(isActiveDevWorkflowRunStatus("Pausing")).toBe(true);
		expect(isActiveDevWorkflowRunStatus("Cancelling")).toBe(true);
		expect(isActiveDevWorkflowRunStatus("Paused")).toBe(true);
		expect(isTerminalDevWorkflowRunStatus("Cancelled")).toBe(true);
		expect(isActiveDevWorkflowRunStatus("Completed")).toBe(false);
	});

	it("treats Blocked as needs-intervention alongside a gate (Y20), not as a passive wait", () => {
		expect(devWorkflowNodeAwaitsHuman("Blocked")).toBe(true);
		expect(devWorkflowNodeAwaitsHuman("WaitingForApproval")).toBe(true);
		expect(devWorkflowNodeAwaitsHuman("Pending")).toBe(false);
	});

	it("grants motion to Running only — a Queued node is waiting for a slot, not working", () => {
		expect(isDevWorkflowNodeInProgress("Running")).toBe(true);
		expect(isDevWorkflowNodeInProgress("Queued")).toBe(false);
		expect(isDevWorkflowNodeInProgress("Pending")).toBe(false);
	});
});

describe("dev-workflow artifact rendering", () => {
	it("maps kinds to editor languages, defaulting to markdown", () => {
		expect(devWorkflowArtifactLanguage("Patch", "text/plain")).toBe("diff");
		expect(devWorkflowArtifactLanguage("TaskPackage", "application/json")).toBe("json");
		expect(devWorkflowArtifactLanguage("Plan", "text/markdown")).toBe("markdown");
		expect(devWorkflowArtifactLanguage(undefined, "application/json")).toBe("json");
		expect(devWorkflowArtifactLanguage(undefined, undefined)).toBe("markdown");
	});

	it("reports undecodable base64 as binary instead of rendering replacement characters", () => {
		expect(decodeDevWorkflowArtifactContent("plain text", false)).toEqual({ text: "plain text", isBinary: false });
		expect(decodeDevWorkflowArtifactContent(btoa("hello"), true)).toEqual({ text: "hello", isBinary: false });
		// 0xFF is not valid UTF-8; the fatal TextDecoder is what turns it into an honest "binary" answer.
		expect(decodeDevWorkflowArtifactContent(btoa("ÿþ"), true).isBinary).toBe(true);
	});
});
